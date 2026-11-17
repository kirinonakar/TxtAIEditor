using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        private readonly AgentContextCompressionService _contextCompressionService;
        private readonly AgentPlanController _planController;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly AgentRunOutputController _runOutputController;
        private readonly AgentRunTranscriptService _runTranscriptService = new();
        private readonly AgentRunWorkspaceResolver _runWorkspaceResolver;
        private readonly AgentRunTextFormatter _runTextFormatter;
        private readonly AgentLlmToolCatalog _llmToolCatalog;
        private readonly AgentResponseInspector _responseInspector;
        private readonly AgentResponseStreamService _responseStreamService;
        private readonly AgentSessionHistoryCoordinator _sessionHistoryCoordinator;
        private readonly Dictionary<string, AgentRunContext> _runningSessions;
        private readonly AgentToolExecutionGate _toolExecutionSessionGate;
        private readonly AsyncLocal<AgentRunContext?> _activeToolRunContext = new();
        private const string SupersededToolResult = "Tool failed: this call was skipped because a new orchestrator instruction arrived.";
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
            AgentContextCompressionService contextCompressionService,
            AgentPlanController planController,
            AgentSessionEditController sessionEditController,
            AgentRunOutputController runOutputController,
            AgentRunWorkspaceResolver runWorkspaceResolver,
            AgentRunTextFormatter runTextFormatter,
            AgentLlmToolCatalog llmToolCatalog,
            AgentResponseInspector responseInspector,
            AgentResponseStreamService responseStreamService,
            AgentSessionHistoryCoordinator sessionHistoryCoordinator,
            Dictionary<string, AgentRunContext> runningSessions,
            AgentToolExecutionGate toolExecutionSessionGate,
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
            _contextCompressionService = contextCompressionService;
            _planController = planController;
            _sessionEditController = sessionEditController;
            _runOutputController = runOutputController;
            _runWorkspaceResolver = runWorkspaceResolver;
            _runTextFormatter = runTextFormatter;
            _llmToolCatalog = llmToolCatalog;
            _responseInspector = responseInspector;
            _responseStreamService = responseStreamService;
            _sessionHistoryCoordinator = sessionHistoryCoordinator;
            _runningSessions = runningSessions;
            _toolExecutionSessionGate = toolExecutionSessionGate;
            _currentSessionIdProvider = currentSessionIdProvider;
            _restoreSessionHistoryState = restoreSessionHistoryState;
            _updateContextStatsImmediate = updateContextStatsImmediate;
        }

        public string? GetActiveRunWorkspaceRoot()
        {
            AgentRunContext? context = _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value;
            if (context?.PinnedWorkspaceRoot is string pinned)
            {
                AgentWorkspaceFileResolver.VerifyPinnedWorkspace(context.WorkspaceRoot, pinned);
            }
            return context?.WorkspaceRoot;
        }

        public AgentRunContext? GetActiveRunContext()
        {
            return _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value;
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
            string transcript = string.Empty;
            string modelTranscript = string.Empty;
            string approvedPlanExecutionPrompt = string.Empty;
            string approvedPlanWorkspaceRoot = activeOpenSession.WorkspaceRoot;

            bool historyReady = false;
            bool responseStreamInProgress = false;
            bool sessionClosed = false;
            AgentSubagentCoordinator? orchestration = null;
            void FinishRun(string? responseLine, AgentRunStatus status = AgentRunStatus.Completed)
            {
                if (string.IsNullOrEmpty(transcript))
                {
                    transcript = conversationTurn + Environment.NewLine;
                    modelTranscript = runContext.ModelSessionHistory.ToString().TrimEnd() +
                        Environment.NewLine + transcript;
                }
                AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                    runContext, conversationTurn, transcript,
                    initialTranscript, responseLine, modelTranscript);
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
                    var workerSessions = new Dictionary<string, AgentOpenSessionState>();
                    orchestration = new AgentSubagentCoordinator(
                        Math.Clamp(config.LlmMaxSubagents, 1, EditorSettings.MaxSubagentLimit),
                        (id, title, instruction, inbox, progress, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            workerSessions.TryGetValue(id, out AgentOpenSessionState? previousState);
                            var workerSession = _openSessionController.EnsureSession(id, previousState);
                            workerSession.Role = AgentSessionRole.Subagent;
                            workerSession.OrchestratorSessionId = runContext.SessionId;
                            workerSession.Title = title;
                            workerSessions[id] = workerSession;
                            if (previousState == null)
                            {
                                runContext.RewindSnapshot?.SubagentSessionIds.Add(id);
                            }
                            return RunSubagentAsync(runContext, workerSettings, currentRunActiveTab, currentRunSelectionSnapshot,
                                workerSession, instruction, inbox, progress, token);
                        },
                        message => _runOutputController.AppendRunActivityAsync(runContext, message),
                        _getString,
                        cancellationToken);
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

                transcript = initialTranscript + conversationTurn.Trim() + Environment.NewLine;
                modelTranscript = transcript;
                string response = string.Empty;

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
                        transcript += orchestration.DrainProgressLog();
                    }
                    string fixedPromptContext = await _uiDispatcher.RunAsync<string>(async () =>
                    {
                        await _mcpController.ApplyPendingPluginChangesAsync(cancellationToken);
                        AgentWorkspaceFileResolver.VerifyPinnedWorkspace(runContext.WorkspaceRoot, runContext.PinnedWorkspaceRoot);
                        runContext.HasEnabledSkills = _skillController.HasSelectedSkills();
                        runContext.HasEnabledMcp = !runContext.IsPlanResearch && _mcpController.HasSelectedMcpServers();
                        return _promptContextService.BuildFixedPromptContextForRun(
                            runContext.PinnedWorkspaceRoot, includeMcp: !runContext.IsPlanResearch);
                    });
                    if (orchestration != null)
                    {
                        fixedPromptContext += "\n\n" + orchestration.BuildInstructions(planningMode);
                    }
                    else if (isSubagent)
                    {
                        fixedPromptContext += "\n\n" + AgentSubagentCoordinator.BuildWorkerInstructions(runContext.IsPlanResearch);
                    }
                    string? queuedFollowUpPrompt = runContext.FollowUps.Take(out Task followUpRequested);
                    if (!string.IsNullOrWhiteSpace(queuedFollowUpPrompt))
                    {
                        string followUpTurn = isSubagent
                            ? $"[user]\n{AgentSessionLabels.OrchestratorPrompt}\n{queuedFollowUpPrompt}"
                            : _promptContextService.BuildConversationTurn(queuedFollowUpPrompt);
                        if (!string.IsNullOrWhiteSpace(followUpTurn))
                        {
                            string followUpPart = Environment.NewLine + Environment.NewLine + followUpTurn.Trim();
                            transcript += followUpPart;
                            modelTranscript += followUpPart;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(followUpPart);
                            conversationTurn = followUpTurn;
                            await _uiDispatcher.RunAsync(() => _updateContextStatsImmediate(true));
                        }

                        string followUpHeader = _runTextFormatter.BuildRunHeader(
                            isSubagent ? queuedFollowUpPrompt : _promptContextService.BuildInstructionDisplay(queuedFollowUpPrompt), isSubagent);
                        await _runOutputController.AppendRunOutputLineAsync(runContext, followUpHeader);
                    }
                    string TakeEventBatch()
                    {
                        string events = orchestration?.DrainModelEvents() ?? string.Empty;
                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(events);
                        return events;
                    }

                    string eventBatch = TakeEventBatch();
                    string currentTranscript = _runTranscriptService.BuildWithEditLedger(
                        modelTranscript + eventBatch,
                        currentTaskStartEditIndex,
                        runContext.SessionEdits);
                    IReadOnlyList<LlmTool> agentTools = _llmToolCatalog.Build(
                        planningMode,
                        runContext.IsPlanResearch ? Array.Empty<AgentMcpToolAlias>() : _mcpController.GetActiveToolAliases(),
                        runContext.HasEnabledSkills);
                    if (orchestration != null)
                    {
                        agentTools = agentTools.Concat(AgentSubagentCoordinator.Tools).ToArray();
                    }
                    else if (isSubagent)
                    {
                        agentTools = agentTools.Where(tool => IsAvailableToSubagent(runContext, tool.Name))
                            .Append(AgentSubagentCoordinator.ProgressTool).ToArray();
                    }
                    IReadOnlyList<LlmMessageAttachment> imageAttachments =
                        _promptContextService.GetImageAttachmentsForRun(runContext);
                    int EstimateCurrentRequestTokens() => AgentContextCompressionService.EstimateModelRequestTokens(
                        runContext.LlmSettings, fixedPromptContext, currentTranscript, currentWorkspaceContext,
                        runSelectionContext, planningMode, runContext.HasEnabledSkills, runContext.HasEnabledMcp,
                        agentTools, imageAttachments);

                    bool compressed = false;
                    int requestTokens = 0;
                    for (int pass = 0; ; pass++)
                    {
                        AgentContextCompressionResult compressionResult = await _contextCompressionService.CompressIfNeededAsync(
                            runContext.LlmSettings,
                            fixedPromptContext,
                            modelTranscript,
                            currentTranscript,
                            currentWorkspaceContext,
                            runSelectionContext,
                            planningMode,
                            runContext.HasEnabledSkills,
                            runContext.HasEnabledMcp,
                            agentTools,
                            imageAttachments,
                            cancellationToken,
                            runContext.RequestTokenCalibration);
                        if (compressionResult.Compressed)
                        {
                            compressed = true;
                            modelTranscript = compressionResult.Transcript;
                        }

                        // An event batch that arrives during compression still goes through
                        // the final budget check. Each request gets at most one bounded batch.
                        bool appendedLateEvents = false;
                        if (pass == 0 && eventBatch.Length == 0 && orchestration != null)
                        {
                            eventBatch = TakeEventBatch();
                            appendedLateEvents = eventBatch.Length > 0;
                        }
                        if (compressionResult.Compressed || appendedLateEvents)
                        {
                            currentTranscript = _runTranscriptService.BuildWithEditLedger(
                                modelTranscript + eventBatch, currentTaskStartEditIndex, runContext.SessionEdits);
                        }

                        if (orchestration == null && !compressed)
                        {
                            break;
                        }

                        requestTokens = EstimateCurrentRequestTokens();
                        if (orchestration == null || !_contextCompressionService.ExceedsInputBudget(
                            runContext.LlmSettings, requestTokens, runContext.RequestTokenCalibration))
                        {
                            break;
                        }

                        if (pass >= 1 || (!compressionResult.Compressed && !appendedLateEvents))
                        {
                            modelTranscript += eventBatch;
                            throw new LlmRequestException(_getString("AgentOrchestratorContextBudgetExceeded",
                                "압축 후에도 오케스트레이터의 입력이 모델 컨텍스트 한도를 초과합니다."));
                        }
                    }

                    modelTranscript += eventBatch;
                    if (compressed)
                    {
                        // The panel token display must show the compressed request that is
                        // actually sent to the model, not the pre-compression transcript size.
                        runContext.ActualRequestTokens = requestTokens;
                        runContext.ActualRequestTokensBase = runContext.CurrentRunTranscriptTokens;
                        string compressionNotice = _getString(
                            "AgentContextCompressedNotice",
                            "context 압축이 시행되었습니다.");
                        transcript +=
                            Environment.NewLine + Environment.NewLine +
                            compressionNotice +
                            Environment.NewLine + Environment.NewLine +
                            AgentRunTranscriptService.CompressedContextStartMarker +
                            Environment.NewLine +
                            modelTranscript.TrimEnd() +
                            Environment.NewLine +
                            AgentRunTranscriptService.CompressedContextEndMarker;
                        await _runOutputController.AppendRunOutputLineAsync(
                            runContext,
                            compressionNotice);
                        await _uiDispatcher.RunAsync(() => _updateContextStatsImmediate(true));
                    }

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
                        currentTranscript,
                        currentWorkspaceContext,
                        runSelectionContext,
                        planningMode,
                        agentTools,
                        imageAttachments,
                        cancellationToken,
                        deferAnswer: deferAnswer);
                    responseStreamInProgress = false;
                    runContext.ToolCancellationToken.ThrowIfCancellationRequested();
                    _ = GetActiveRunWorkspaceRoot();

                    if (!string.IsNullOrWhiteSpace(runContext.PendingVisionFallbackContext))
                    {
                        string fallbackTranscriptPart =
                            "\n\n" + runContext.PendingVisionFallbackContext.Trim();
                        transcript += fallbackTranscriptPart;
                        modelTranscript += fallbackTranscriptPart;
                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(fallbackTranscriptPart);
                        runContext.PendingVisionFallbackContext = string.Empty;
                        await _uiDispatcher.RunAsync(() => _updateContextStatsImmediate(true));
                    }

                    if (isSubagent && followUpRequested.IsCompleted)
                    {
                        await _runOutputController.StopRunThinkingActivityAsync(runContext);
                        continue;
                    }

                    response = streamResult.Response;
                    bool truncated = streamResult.Truncated;
                    string cleanResponse = streamResult.CleanResponse;
                    int printedLength = streamResult.PrintedLength;
                    bool heldPotentialToolCallText = streamResult.HeldPotentialToolCallText;
                    bool visibleTextFlushed = streamResult.VisibleTextFlushed;
                    bool usedNativeFunctionToolCall = streamResult.UsedNativeFunctionToolCall;
                    string responseForTranscript = streamResult.ResponseWithoutThinking;
                    string retainedThinking = runContext.LlmSettings.LlmRetainThinking
                        ? streamResult.ThinkingText
                        : string.Empty;
                    string retainedThinkingSection =
                        AgentThinkingTranscriptFormatter.BuildThinkingSection(retainedThinking);
                    string retainedThinkingTranscriptPart = string.IsNullOrEmpty(retainedThinkingSection)
                        ? string.Empty
                        : Environment.NewLine + Environment.NewLine + retainedThinkingSection;

                    if (truncated)
                    {
                        await _runOutputController.StopRunThinkingActivityAsync(runContext);

                        if (retries.TryRetry(AgentRetryReason.Truncation))
                        {
                            string continuationNote =
                                "\n\n[Agent response truncated]\n" +
                                "Your previous response was cut off due to the output token limit. " +
                                "Continue exactly from where you stopped. Do not repeat what you already wrote. " +
                                "If you were about to write a tool_call, write it now.";
                            string retryDetail = _runTranscriptService.BuildRetryDetail(
                                "truncated_response",
                                responseForTranscript,
                                continuationNote);
                            retryDetail = retainedThinkingTranscriptPart + retryDetail;

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += retryDetail;
                                modelTranscript += retryDetail;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryDetail);
                                _updateContextStatsImmediate(true);
                            });
                            string truncationRetryMessage = _getString(
                                "AgentActivityTruncatedRetry",
                                "응답이 잘려 이어서 작성합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, truncationRetryMessage);
                            continue;
                        }
                        throw new LlmRequestException(_getString("AgentTruncatedRetryLimit", "응답 잘림이 반복되어 Agent 실행을 중단했습니다. 다시 실행해 주세요."));
                    }

                    if (string.IsNullOrWhiteSpace(responseForTranscript))
                    {
                        await _runOutputController.StopRunThinkingActivityAsync(runContext);

                        if (retries.TryRetry(AgentRetryReason.EmptyResponse))
                        {
                            string retryNote =
                                "\n\n[Agent empty response]\n" +
                                "The model returned no visible content. Continue with a final tool_call or a final answer.";
                            string retryDetail = _runTranscriptService.BuildRetryDetail(
                                "empty_response",
                                string.Empty,
                                retryNote);
                            retryDetail = retainedThinkingTranscriptPart + retryDetail;

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += retryDetail;
                                modelTranscript += retryDetail;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryDetail);
                                _updateContextStatsImmediate(true);
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

                        throw new LlmRequestException(emptyResponseMessage);
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

                    if ((planningMode || deferAnswer) && responseHasToolSyntax && !responseLooksLikeToolResultReplay)
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
                    else if (!planningMode &&
                        responseHasToolSyntax &&
                        !responseLooksLikeToolResultReplay &&
                        !visibleTextFlushed)
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
                    else if (!planningMode && !deferAnswer && !responseRequiresToolHandling && heldPotentialToolCallText && !visibleTextFlushed && !string.IsNullOrEmpty(cleanResponse))
                    {
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, cleanResponse);
                    }
                    else if (!planningMode && !deferAnswer && printedLength < endLength)
                    {
                        string remainingText = cleanResponse.Substring(printedLength, endLength - printedLength);
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, remainingText);
                    }

                    runContext.ToolCancellationToken.ThrowIfCancellationRequested();

                    bool parsedToolCall = AgentToolCallParser.TryParseMulti(response, out List<AgentToolCallParser.ToolCallInfo> toolCalls);
                    if (responseLooksLikeToolResultReplay || !parsedToolCall || toolCalls.Count == 0)
                    {
                        if (responseHasToolSyntax || responseLooksLikeToolResultReplay)
                        {
                            bool retryAllowed = retries.TryRetry(AgentRetryReason.ToolCallFormat);
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
                                responseForTranscript,
                                retryNote);
                            runContext.RetryDebugHistory.AppendLine(retryDetail);
                            string retryPromptContext =
                                retainedThinkingTranscriptPart +
                                "\n\n[Retry instruction]\n" +
                                retryNote.Trim();
                            transcript += retryPromptContext;
                            modelTranscript += retryPromptContext;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryPromptContext);

                            string retryMessage = _getString(
                                "AgentToolCallFormatRetry",
                                "도구 호출을 해석하지 못해 다시 요청합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
                            if (_runOutputController.IsSessionVisible(runContext.SessionId))
                            {
                                _updateContextStatsImmediate(true);
                            }

                            if (!retryAllowed)
                            {
                                string limitMessage = _getString(
                                    "AgentToolCallFormatRetryLimit",
                                    "도구 호출 형식 오류가 반복되어 Agent 실행을 중단했습니다. 작업을 다시 실행해 주세요.");
                                await _runOutputController.AppendRunActivityAsync(runContext, limitMessage);
                                await _runOutputController.AppendRunOutputLineAsync(runContext, limitMessage);

                                throw new LlmRequestException(limitMessage);
                            }

                            continue;
                        }

                        if (orchestration?.NeedsModelReview == true)
                        {
                            await orchestration.WaitForEventAsync(followUpRequested);
                            continue;
                        }

                        if (isSubagent && !runContext.FollowUps.TryCloseIfEmpty())
                        {
                            continue;
                        }

                        if (planningMode)
                        {
                            bool retryAllowed = retries.TryRetry(AgentRetryReason.MakePlan);
                            string retryNote = BuildMakePlanRequiredRetryNote(runContext);
                            string retryDetail = _runTranscriptService.BuildRetryDetail(
                                "make_plan_required",
                                responseForTranscript,
                                retryNote);
                            retryDetail = retainedThinkingTranscriptPart + retryDetail;

                            transcript += retryDetail;
                            modelTranscript += retryDetail;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryDetail);

                            string retryMessage = _getString(
                                "AgentMakePlanRequired",
                                "계획 모드에서는 make_plan 도구로 계획서를 저장해야 합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
                            if (_runOutputController.IsSessionVisible(runContext.SessionId))
                            {
                                _updateContextStatsImmediate(true);
                            }

                            if (!retryAllowed)
                            {
                                string limitMessage = _getString(
                                    "AgentMakePlanRetryLimit",
                                    "make_plan 도구 호출이 생성되지 않아 계획 모드를 중단했습니다. 다시 실행해 주세요.");
                                await _runOutputController.AppendRunActivityAsync(runContext, limitMessage);
                                await _runOutputController.AppendRunOutputLineAsync(runContext, limitMessage);

                                throw new LlmRequestException(limitMessage);
                            }

                            continue;
                        }

                        if (!visibleTextFlushed && !string.IsNullOrWhiteSpace(response))
                        {
                            await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, response);
                        }

                        retries.Reset(AgentRetryReason.ToolCallFormat);
                        runContext.LastAnswerText = AgentRunTextFormatter.BuildLastAnswerText(response, cleanResponse, runContext.LlmSettings.LlmAgentVerbose);
                        await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                        if (runContext.StreamToTab)
                        {
                            await _runOutputController.EndStreamedAnswerAsync(runContext);
                        }

                        string finalAnswerLog = AgentThinkingTranscriptFormatter.BuildFinalAnswerLog(
                            responseForTranscript,
                            retainedThinking);
                        FinishRun(finalAnswerLog);

                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(finalAnswerLog);
                        completed = true;
                        break;
                    }

                    bool stopAfterLoopGuard = false;
                    toolCalls = OrderToolCallsForExecution(toolCalls, runContext.WorkspaceRoot);
                    if (planningMode && orchestration != null)
                    {
                        toolCalls = toolCalls.OrderBy(call => NormalizeToolName(call.ToolName) == "make_plan").ToList();
                    }
                    var toolCallResults = new List<(string Name, JsonElement Args, string Result, string ResultForTranscript, bool Skipped, string NormalizedName)>();
                    bool hasWebSearchCallInBatch = false;
                    bool waitedForSubagentInBatch = false;

                    int parallelStart = 0;
                    string[] parallelResults = [];
                    for (int toolIndex = 0; toolIndex < toolCalls.Count; toolIndex++)
                    {
                        runContext.ToolCancellationToken.ThrowIfCancellationRequested();

                        if (toolIndex >= parallelStart + parallelResults.Length)
                        {
                            parallelStart = toolIndex;
                            parallelResults = [];
                            if (!isSubagent || !followUpRequested.IsCompleted)
                            {
                                var reads = GetIndependentReads(toolCalls, toolIndex, toolInvocationCounts);
                                if (reads.Count > 1)
                                {
                                    parallelResults = await Task.WhenAll(reads.Select(read => ExecuteReadToolAsync(
                                        runContext, read.ToolName, NormalizeToolName(read.ToolName), read.Arguments,
                                        isSubagent, followUpRequested, cancellationToken, trackFilePath: false)));
                                }
                            }
                        }

                        var tc = toolCalls[toolIndex];
                        string currentToolName = tc.ToolName;
                        JsonElement currentArguments = tc.Arguments;

                        string normalizedToolName = NormalizeToolName(currentToolName);
                        if (normalizedToolName == "apply_patch")
                        {
                            currentArguments = SanitizeApplyPatchArguments(currentArguments);
                        }

                        if (toolCalls.Count > 1 && normalizedToolName == "web_search")
                        {
                            if (hasWebSearchCallInBatch)
                            {
                                int delayMilliseconds = Random.Shared.Next(500, 2001);
                                await Task.Delay(delayMilliseconds, cancellationToken);
                            }

                            hasWebSearchCallInBatch = true;
                        }

                        string toolInvocationKey = BuildToolInvocationKey(normalizedToolName, currentArguments);
                        toolInvocationCounts[toolInvocationKey] = toolInvocationCounts.TryGetValue(toolInvocationKey, out int prevCount) ? prevCount + 1 : 1;
                        int currentInvocationCount = toolInvocationCounts[toolInvocationKey];
                        string toolResult;
                        string toolResultForTranscript;
                        bool interruptedSubagentWait = false;
                        retries.Reset(AgentRetryReason.ToolCallFormat);

                        if (toolIndex - parallelStart < parallelResults.Length)
                        {
                            toolResult = parallelResults[toolIndex - parallelStart];
                            _toolExecutionController.TrackFileToolPath(normalizedToolName, currentArguments, toolResult);
                            toolResultForTranscript = toolResult;
                        }
                        else if (isSubagent && followUpRequested.IsCompleted)
                        {
                            toolResult = SupersededToolResult;
                            toolResultForTranscript = toolResult;
                        }
                        else if (AgentSubagentCoordinator.IsCoordinationTool(normalizedToolName))
                        {
                            bool isWait = normalizedToolName == AgentSubagentCoordinator.WaitTool;
                            if (isWait && waitedForSubagentInBatch)
                            {
                                toolResult = "Wait deferred. Review the subagent result already returned in this response before waiting again.";
                            }
                            else
                            {
                                toolResult = orchestration != null
                                    ? await orchestration.ExecuteAsync(normalizedToolName, currentArguments, followUpRequested)
                                    : await ExecuteSubagentProgressAsync(runContext, normalizedToolName, currentArguments);
                                waitedForSubagentInBatch |= isWait;
                                interruptedSubagentWait = isWait && toolResult == AgentSubagentCoordinator.WaitInterruptedResult;
                            }
                            toolResultForTranscript = toolResult;
                        }
                        else if (isSubagent && !IsAvailableToSubagent(runContext, normalizedToolName))
                        {
                            toolResult = $"Tool failed: {normalizedToolName} is unavailable for this subagent run.";
                            toolResultForTranscript = toolResult;
                        }
                        else if (planningMode && normalizedToolName == "make_plan" && orchestration != null &&
                            (waitedForSubagentInBatch || orchestration.NeedsModelReview))
                        {
                            toolResult = "make_plan failed: review the subagent events before submitting the revised plan.";
                            if (!waitedForSubagentInBatch)
                            {
                                await orchestration.WaitForEventAsync(followUpRequested);
                                interruptedSubagentWait = followUpRequested.IsCompleted;
                            }
                            toolResultForTranscript = toolResult;
                        }
                        else if (!planningMode && normalizedToolName == "make_plan")
                        {
                            toolResult = "make_plan failed: this tool is only available when planning mode is enabled.";
                            toolResultForTranscript = toolResult;
                        }
                        else if (planningMode && IsMutatingTool(normalizedToolName) &&
                            normalizedToolName != "make_plan" &&
                            !IsAllowedPlanRevisionEdit(runContext, normalizedToolName, currentArguments))
                        {
                            toolResult = BuildPlanningMutationBlockedMessage(runContext);
                            toolResultForTranscript = toolResult;
                        }
                        else if (_toolExecutionController.IsFileRead(normalizedToolName) || _toolExecutionController.IsWebRead(normalizedToolName))
                        {
                            toolResult = await ExecuteReadToolAsync(runContext, currentToolName, normalizedToolName,
                                currentArguments, isSubagent, followUpRequested, cancellationToken);
                            toolResultForTranscript = toolResult;
                        }
                        else
                        {
                            string[] lockPaths = _toolExecutionController.GetFileMutationLockPaths(normalizedToolName, ref currentArguments);
                            using IDisposable lease = lockPaths.Length > 0
                                ? await _toolExecutionSessionGate.EnterFilesAsync(lockPaths, cancellationToken)
                                : await _toolExecutionSessionGate.EnterExclusiveAsync(cancellationToken);
                            runContext.ToolCancellationToken.ThrowIfCancellationRequested();
                            _ = GetActiveRunWorkspaceRoot();
                            _activeToolRunContext.Value = runContext;
                            try
                            {
                                await _uiDispatcher.RunAsync(() => _sessionEditController.BeginToolRecording(runContext.SessionId));
                                try
                                {
                                    if (isSubagent && followUpRequested.IsCompleted)
                                    {
                                        toolResult = SupersededToolResult;
                                    }
                                    else if (planningMode && IsMutatingTool(normalizedToolName) &&
                                        normalizedToolName != "make_plan" &&
                                        !IsAllowedPlanRevisionEdit(runContext, normalizedToolName, currentArguments))
                                    {
                                        toolResult = BuildPlanningMutationBlockedMessage(runContext);
                                    }
                                    else
                                    {
                                        toolResult = await _toolExecutionController.ExecuteAsync(
                                            currentToolName,
                                            currentArguments,
                                            runContext.LlmSettings.LlmAgentVerbose,
                                            runContext.ToolCancellationToken);
                                    }
                                }
                                finally
                                {
                                    await _uiDispatcher.RunAsync(() =>
                                    {
                                        runContext.SessionEdits = _sessionEditController.EndToolRecording(runContext.SessionId);
                                        activeOpenSession.SessionEdits = runContext.SessionEdits.ToList();
                                    });
                                }
                            }
                            finally
                            {
                                _activeToolRunContext.Value = null;
                            }
                            toolResultForTranscript = toolResult;
                            if (IsSuccessfulToolResult(toolResult))
                            {
                                if (IsMutatingTool(normalizedToolName) ||
                                    string.Equals(normalizedToolName, "run_powershell", StringComparison.Ordinal))
                                {
                                    toolInvocationCounts.Clear();
                                }
                            }
                        }

                        if (!interruptedSubagentWait && IsSuccessfulToolResult(toolResult))
                        {
                            toolResultForTranscript = $"{toolResult}\n\n[Tool execution status: success.]";
                            if (normalizedToolName == "read_file" && currentInvocationCount >= 4)
                            {
                                toolResultForTranscript += $"\n\n[Loop guard] You have called read_file with the exact same parameters {currentInvocationCount} times in this request. Do not repeat this tool call meaninglessly. Stop and think carefully about the information already retrieved, choose a different next action, or write your final answer.";
                                if (currentInvocationCount >= 6)
                                {
                                    stopAfterLoopGuard = true;
                                }
                            }
                        }

                        toolResultForTranscript = _runTranscriptService.AddToolTimingNote(
                            normalizedToolName,
                            toolResultForTranscript,
                            toolResult);

                        toolCallResults.Add((currentToolName, currentArguments, toolResult, toolResultForTranscript, interruptedSubagentWait, normalizedToolName));

                        if (interruptedSubagentWait || stopAfterLoopGuard ||
                            (!IsSuccessfulToolResult(toolResult) && toolCalls.Count == 1))
                        {
                            break;
                        }
                    }

                    runContext.ToolCancellationToken.ThrowIfCancellationRequested();

                    currentWorkspaceContext = await _uiDispatcher.RunAsync(() =>
                        _promptContextService.BuildWorkspaceContextAsync(
                            conversationTurn,
                            currentRunActiveTab,
                            currentRunSelectionSnapshot,
                            runContext.Attachments,
                            runContext.PinnedWorkspaceRoot,
                            cancellationToken));
                    var transcriptUpdate = await Task.Run(() =>
                    {
                        var addedPartBuilder = new StringBuilder();
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine();
                        if (!string.IsNullOrEmpty(retainedThinkingSection))
                        {
                            addedPartBuilder.AppendLine(retainedThinkingSection);
                            addedPartBuilder.AppendLine();
                        }
                        addedPartBuilder.AppendLine(usedNativeFunctionToolCall
                            ? "[assistant: tool call] (native function)"
                            : "[assistant: tool call] (text)");
                        addedPartBuilder.AppendLine(responseForTranscript);

                        foreach (var tcRes in toolCallResults)
                        {
                            addedPartBuilder.AppendLine();
                            addedPartBuilder.AppendLine($"[tool: {tcRes.Name} arguments]");
                            addedPartBuilder.AppendLine(tcRes.Args.GetRawText());
                            addedPartBuilder.AppendLine();
                            addedPartBuilder.AppendLine($"[tool: {tcRes.Name} result]");
                            addedPartBuilder.AppendLine(tcRes.ResultForTranscript);
                        }

                        string addedPart = addedPartBuilder.ToString();
                        return (Text: addedPart, Tokens: AgentTokenEstimator.Estimate(addedPart));
                    }, cancellationToken);
                    transcript += transcriptUpdate.Text;
                    if (orchestration != null)
                    {
                        transcript += orchestration.DrainProgressLog();
                    }
                    modelTranscript += transcriptUpdate.Text;
                    runContext.CurrentRunTranscriptTokens += transcriptUpdate.Tokens;
                    await _uiDispatcher.RunAsync(() => _updateContextStatsImmediate(true));
                    
                    foreach (var tcRes in toolCallResults)
                    {
                        if (tcRes.Skipped)
                        {
                            continue;
                        }

                        bool toolSucceeded = IsSuccessfulToolResult(tcRes.Result);
                        if (isSubagent && tcRes.NormalizedName == AgentSubagentCoordinator.ReportProgressTool && toolSucceeded)
                        {
                            string progressLine = AgentSubagentCoordinator.FormatProgressLine(GetStringArgument(tcRes.Args, "message"));
                            transcript += "\n" + AgentSubagentCoordinator.ProgressMarker + progressLine + "\n";
                        }
                        if (!runContext.LlmSettings.LlmAgentVerbose && toolSucceeded &&
                            AgentSubagentCoordinator.IsCoordinationTool(tcRes.NormalizedName))
                        {
                            continue;
                        }
                        string displayResult = _toolExecutionController.FormatDisplayResult(
                            tcRes.NormalizedName,
                            tcRes.Args,
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

                    var makePlanRes = toolCallResults.FirstOrDefault(tc => tc.NormalizedName == "make_plan" && IsSuccessfulToolResult(tc.Result));
                    if (planningMode && makePlanRes.Name != null)
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

                    if (stopAfterLoopGuard)
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
                    if (toolCalls.Count == 1 && !isSubagent && orchestration == null)
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
                            transcript += verificationPart;
                            modelTranscript += verificationPart;

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
                    transcript += interruptedTranscriptPart;
                    modelTranscript += interruptedTranscriptPart;
                    runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(interruptedTranscriptPart);
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
                    runContext, conversationTurn, transcript, initialTranscript,
                    modelTranscript, responseStreamInProgress, failureMessage);
                historyReady = true;
            }
            finally
            {
                if (orchestration != null)
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
                                _ = ShowSubagentProgressAsync(delayed, _getString("AgentSubagentShutdownDelayed",
                                    "종료 요청 후에도 작업이 정리 중입니다."));
                            }
                            _ = ObserveSubagentShutdownAsync(worker);
                        }
                        string progressLog = orchestration.DrainProgressLog();
                        if (progressLog.Length > 0)
                        {
                            AgentRunTranscriptRecorder.AppendLine(runContext, progressLog);
                        }
                    }
                }

                await _uiDispatcher.RunAsync(async () => await _runOutputController.FinishStreamToTabAsync(runContext));
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
                    runContext.PinnedWorkspaceRoot, useSubagents);
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

        private List<AgentToolCallParser.ToolCallInfo> GetIndependentReads(
            IReadOnlyList<AgentToolCallParser.ToolCallInfo> calls, int start,
            Dictionary<string, int> invocationCounts)
        {
            var reads = new List<AgentToolCallParser.ToolCallInfo>();
            var seenFileReads = new HashSet<string>(StringComparer.Ordinal);
            for (int index = start; index < calls.Count && reads.Count < AgentToolExecutionGate.ParallelLimit; index++)
            {
                var call = calls[index];
                string name = NormalizeToolName(call.ToolName);
                if (!_toolExecutionController.CanRunInReadBatch(name))
                {
                    break;
                }
                if (name == "read_file")
                {
                    string key = BuildToolInvocationKey(name, call.Arguments);
                    // The normal result loop must handle repeated calls and stop at its loop guard.
                    if (!seenFileReads.Add(key) || invocationCounts.GetValueOrDefault(key) >= 5)
                    {
                        break;
                    }
                }
                reads.Add(call);
            }
            return reads;
        }

        private async Task<string> ExecuteReadToolAsync(
            AgentRunContext context, string toolName, string normalizedName, JsonElement arguments,
            bool isSubagent, Task followUpRequested, CancellationToken token, bool trackFilePath = true)
        {
            using IDisposable lease = _toolExecutionController.IsWebRead(normalizedName)
                ? await _toolExecutionSessionGate.EnterWebAsync(token)
                : await _toolExecutionSessionGate.EnterFileReadAsync(
                    _toolExecutionController.GetFileReadLockPath(normalizedName, arguments), token);
            context.ToolCancellationToken.ThrowIfCancellationRequested();
            _ = GetActiveRunWorkspaceRoot();
            if (isSubagent && followUpRequested.IsCompleted)
            {
                return SupersededToolResult;
            }

            _activeToolRunContext.Value = context;
            try
            {
                return await _toolExecutionController.ExecuteAsync(
                    toolName, arguments, context.LlmSettings.LlmAgentVerbose, context.ToolCancellationToken, trackFilePath);
            }
            finally
            {
                _activeToolRunContext.Value = null;
            }
        }

        private List<AgentToolCallParser.ToolCallInfo> OrderToolCallsForExecution(
            IReadOnlyList<AgentToolCallParser.ToolCallInfo> toolCalls,
            string workspaceRoot)
        {
            var orderedToolCalls = toolCalls.ToList();
            var lineByIndex = new Dictionary<int, int>();
            var indexesByPath = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < orderedToolCalls.Count; index++)
            {
                if (!TryGetFileEditOrderingKey(
                        orderedToolCalls[index],
                        workspaceRoot,
                        out string canonicalPath,
                        out int startLine))
                {
                    continue;
                }

                lineByIndex[index] = startLine;
                if (!indexesByPath.TryGetValue(canonicalPath, out List<int>? indexes))
                {
                    indexes = new List<int>();
                    indexesByPath[canonicalPath] = indexes;
                }

                indexes.Add(index);
            }

            foreach (List<int> indexes in indexesByPath.Values)
            {
                if (indexes.Count < 2)
                {
                    continue;
                }

                List<AgentToolCallParser.ToolCallInfo> sortedCalls = indexes
                    .OrderByDescending(index => lineByIndex[index])
                    .ThenBy(index => index)
                    .Select(index => orderedToolCalls[index])
                    .ToList();

                for (int slot = 0; slot < indexes.Count; slot++)
                {
                    orderedToolCalls[indexes[slot]] = sortedCalls[slot];
                }
            }

            return orderedToolCalls;
        }

        private bool TryGetFileEditOrderingKey(
            AgentToolCallParser.ToolCallInfo toolCall,
            string workspaceRoot,
            out string canonicalPath,
            out int startLine)
        {
            canonicalPath = string.Empty;
            startLine = 0;

            string normalizedToolName = NormalizeToolName(toolCall.ToolName);
            if (!IsFileEditingTool(normalizedToolName))
            {
                return false;
            }

            int? candidateStartLine = normalizedToolName switch
            {
                "replace_range" => TryGetPositiveIntArgument(
                    toolCall.Arguments,
                    "startLine",
                    "start_line"),
                "search_replace" => TryGetPositiveIntArgument(
                    toolCall.Arguments,
                    "startLine",
                    "start_line") ?? TryGetPositiveIntArgument(
                    toolCall.Arguments,
                    "endLine",
                    "end_line"),
                "apply_patch" => GetHighestPatchStartLine(toolCall.Arguments),
                _ => null
            };

            if (!candidateStartLine.HasValue)
            {
                return false;
            }

            string path;
            try
            {
                path = _fileToolController.GetEditPathArgument(toolCall.Arguments);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string root = string.IsNullOrWhiteSpace(workspaceRoot)
                    ? _fileTools.WorkspaceRoot
                    : workspaceRoot;
                string fullPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(root, path);
                canonicalPath = Path.GetFullPath(fullPath);
                startLine = candidateStartLine.Value;
                return startLine >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static int? TryGetPositiveIntArgument(JsonElement arguments, params string[] names)
        {
            foreach (string name in names)
            {
                if (TryGetIntArgument(arguments, name, out int value) && value > 0)
                {
                    return value;
                }
            }

            return null;
        }

        private static int? GetHighestPatchStartLine(JsonElement arguments)
        {
            string patch = GetFirstStringArgument(arguments, "patch", "patchText", "diff", "content");
            if (string.IsNullOrWhiteSpace(patch))
            {
                return null;
            }

            int highestStartLine = 0;
            bool foundHunk = false;
            foreach (Match match in Regex.Matches(patch, @"@@\s*-(\d+)", RegexOptions.CultureInvariant))
            {
                if (match.Groups.Count <= 1 ||
                    !int.TryParse(match.Groups[1].Value, out int startLine))
                {
                    continue;
                }

                foundHunk = true;
                highestStartLine = Math.Max(highestStartLine, startLine);
            }

            return foundHunk ? highestStartLine : null;
        }

        private bool IsAllowedPlanRevisionEdit(
            AgentRunContext runContext,
            string normalizedToolName,
            JsonElement arguments)
        {
            if (string.IsNullOrWhiteSpace(runContext.PendingPlanPath) ||
                !IsPlanRevisionEditTool(normalizedToolName))
            {
                return false;
            }

            string targetPath = GetFirstStringArgument(arguments, "path", "file", "filePath", "file_path");
            try
            {
                _fileTools.VerifyWorkspaceWritePath(runContext.PendingPlanPath);
                string[] actualTargets = normalizedToolName == "apply_patch"
                    ? AgentFileEditToolService.GetPatchPaths(targetPath, GetFirstStringArgument(arguments, "patch", "patchText", "diff", "content"))
                    : [targetPath];
                return actualTargets.Length > 0 && actualTargets.All(path =>
                    AgentWorkspaceFileResolver.SamePhysicalPath(_fileTools.ResolveToolPath(path), runContext.PendingPlanPath));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string BuildPlanningMutationBlockedMessage(AgentRunContext runContext)
        {
            if (!string.IsNullOrWhiteSpace(runContext.PendingPlanPath))
            {
                return
                    "blocked: in this plan-revision run you may edit only the pending plan file with file edit tools: " +
                    runContext.PendingPlanPath +
                    ". Use apply_patch, search_replace, replace_range, overwrite_file, append_to_file, or insert_to_file with that path, then call make_plan with the path to request approval again.";
            }

            return
                "blocked: planning mode is plan-only and cannot run file/editor mutation tools. " +
                "Continue with safe inspection if needed, or write the detailed Markdown plan as the final answer.";
        }

        private static string BuildMakePlanRequiredRetryNote(AgentRunContext runContext)
        {
            if (!string.IsNullOrWhiteSpace(runContext.PendingPlanPath))
            {
                string examplePlanPath = runContext.PendingPlanPath.Replace(Path.DirectorySeparatorChar, '/');
                return
                    "\n\n[Planning mode make_plan required]\n" +
                    "Do not answer with the plan as plain text. Finish the revision and include exactly one make_plan tool_call with the revised plan file path:\n" +
                    $"<tool_call>{{\"name\":\"make_plan\",\"arguments\":{{\"path\":\"{examplePlanPath}\"}}}}</tool_call>";
            }

            return
                "\n\n[Planning mode make_plan required]\n" +
                "Do not answer with the plan as plain text. Save it by including exactly one make_plan tool_call, using the Markdown plan as the markdown argument. Do not include a path or filename.";
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

        private static bool IsAvailableToSubagent(AgentRunContext context, string toolName)
        {
            if (toolName is "make_plan" or "open_file" or "insert_text" or "create_tab" or "edit_tab" or "save_tab")
            {
                return false;
            }

            return !context.IsPlanResearch || toolName is "list_files" or "search_text" or "run_rg" or "run_rga"
                or "read_file" or "read_image" or "web_search" or "web_fetch" or "web_fetch_exa" or "skill_use";
        }

        private async Task<string> ExecuteSubagentProgressAsync(AgentRunContext context, string name, JsonElement arguments)
        {
            if (name != AgentSubagentCoordinator.ReportProgressTool || context.ReportToOrchestrator == null)
            {
                return $"Unknown tool: {name}";
            }

            string message = GetStringArgument(arguments, "message").Trim();
            if (message.Length == 0)
            {
                return "Tool failed: message is required.";
            }

            string kind = GetStringArgument(arguments, "kind").Trim().ToLowerInvariant();
            if (kind.Length == 0)
            {
                kind = "progress";
            }
            if (kind is not ("progress" or "finding" or "blocker"))
            {
                return "Tool failed: kind must be progress, finding, or blocker.";
            }

            string outcome = await context.ReportToOrchestrator(message, kind);
            _ = ShowSubagentProgressAsync(context, message);
            return outcome;
        }

        private async Task ShowSubagentProgressAsync(AgentRunContext context, string message)
        {
            try
            {
                await _runOutputController.AppendRunActivityAsync(context, AgentSubagentCoordinator.FormatProgressLine(message));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        private async Task StartApprovedPlanSessionAsync(string executionPrompt, string workspaceRoot,
            string? workspacePin, bool useSubagents)
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

        public void AppendActivity(string message)
        {
            AgentRunContext? context = _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value;
            if (context != null)
            {
                _ = _runOutputController.AppendRunActivityAsync(context, message);
                return;
            }

            _openSessionController.AppendActivityToCurrentSession(message);
        }
    }
}
