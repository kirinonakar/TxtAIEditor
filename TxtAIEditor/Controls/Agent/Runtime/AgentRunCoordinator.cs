using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
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
        private readonly SemaphoreSlim _toolExecutionSessionGate;
        private readonly AsyncLocal<AgentRunContext?> _activeToolRunContext = new();
        private readonly AsyncLocal<AgentRunContext?> _activeWorkspaceRunContext = new();
        private readonly Func<string> _currentSessionIdProvider;
        private readonly Action<string, double, double> _restoreSessionHistoryState;
        private readonly Action<bool> _updateContextStatsImmediate;
        private readonly List<LlmMessageAttachment> _currentRunImageToolAttachments = new();

        private bool _isRunning;
        private CancellationTokenSource? _runCancellation;
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
            SemaphoreSlim toolExecutionSessionGate,
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
            return string.IsNullOrWhiteSpace(context?.WorkspaceRoot)
                ? null
                : context.WorkspaceRoot;
        }

        public AgentRunContext? GetActiveRunContext()
        {
            return _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value;
        }

        public double GetCurrentRunTranscriptTokens()
        {
            return GetActiveRunContext()?.CurrentRunTranscriptTokens ?? _currentRunTranscriptTokens;
        }

        public void RestoreCurrentRunTranscriptTokens(double currentRunTranscriptTokens)
        {
            _currentRunTranscriptTokens = currentRunTranscriptTokens;
        }

        private Task<OpenedTab?> CaptureActiveTabForRunAsync()
        {
            return _uiDispatcher.RunAsync(() =>
                _selectionContextController.CaptureActiveTabForRun(_openSessionController.IsCurrentSessionRunning()));
        }

        public async Task RunAgentAsync()
        {
            if (_runningSessions.ContainsKey(_currentSessionIdProvider()))
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
            string conversationTurn = _promptContextService.BuildConversationTurn(requestedPlanningMode
                ? AgentPlanController.BuildPlanningModeRequest(userInstruction, targetLanguage)
                : userInstruction);
            if (string.IsNullOrWhiteSpace(conversationTurn))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentEmptyPrompt", "Agent에게 맡길 작업을 입력해 주세요."));
                return;
            }

            var activeOpenSession = _openSessionController.EnsureSession(_currentSessionIdProvider());
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
            string modelTranscript = string.Empty;
            string approvedPlanExecutionPrompt = string.Empty;
            string approvedPlanWorkspaceRoot = activeOpenSession.WorkspaceRoot;

            Task PersistRunSessionToHistoryAsync()
            {
                return _sessionHistoryCoordinator.SaveRunSessionToHistoryAsync(runContext, userInstruction);
            }

            _activeWorkspaceRunContext.Value = runContext;
            try
            {
                string fixedPromptContext = _promptContextService.BuildFixedPromptContext();
                OpenedTab? currentRunActiveTab = await CaptureActiveTabForRunAsync();
                AgentSelectionSnapshot currentRunSelectionSnapshot = _selectionContextController.CaptureSelectionForRun(_isRunning);
                runContext.StreamToTabTargetTabId = currentRunActiveTab?.Id;
                _fileToolController.SetRunContext(currentRunSelectionSnapshot, currentRunActiveTab);
                string currentWorkspaceContext = _promptContextService.BuildWorkspaceContext(
                    conversationTurn,
                    currentRunActiveTab,
                    currentRunSelectionSnapshot,
                    runContext.Attachments,
                    runContext.WorkspaceRoot);
                string runSelectionContext = _selectionContextController.BuildSelectionContext(currentRunSelectionSnapshot);
                runContext.PlanWorkspaceContext = currentWorkspaceContext;
                runContext.PlanSelectionContext = runSelectionContext;
                var initialTranscriptBuilder = new StringBuilder();
                string sessionHistoryForPrompt = _promptContextService.BuildSessionHistoryForPrompt(
                    conversationTurn,
                    currentWorkspaceContext,
                    runSelectionContext);
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
                        modelTranscript,
                        currentTaskStartEditIndex,
                        runContext.SessionEdits);
                    IReadOnlyList<LlmTool> agentTools = _llmToolCatalog.Build(
                        planningMode,
                        _mcpController.GetActiveToolAliases(),
                        runContext.HasEnabledSkills);
                    IReadOnlyList<LlmMessageAttachment> imageAttachments =
                        _promptContextService.GetImageAttachmentsForRun(runContext);
                    AgentContextCompressionResult compressionResult =
                        await _contextCompressionService.CompressIfNeededAsync(
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
                            cancellationToken);
                    if (compressionResult.Compressed)
                    {
                        modelTranscript = compressionResult.Transcript;
                        currentTranscript = _runTranscriptService.BuildWithEditLedger(
                            modelTranscript,
                            currentTaskStartEditIndex,
                            runContext.SessionEdits);
                        string compressionNotice = _getString(
                            "AgentContextCompressedNotice",
                            "context 압축이 시행되었습니다.");
                        transcript += Environment.NewLine + Environment.NewLine + compressionNotice;
                        await _runOutputController.AppendRunOutputLineAsync(
                            runContext,
                            compressionNotice);
                        await _uiDispatcher.RunAsync(() => _updateContextStatsImmediate(true));
                    }

                    AgentResponseStreamResult streamResult = await _responseStreamService.RunAsync(
                        runContext,
                        fixedPromptContext,
                        currentTranscript,
                        currentWorkspaceContext,
                        runSelectionContext,
                        planningMode,
                        agentTools,
                        imageAttachments,
                        cancellationToken);

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

                        AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                            runContext,
                            conversationTurn,
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

                            if (toolCallFormatRetryCount > maxToolCallFormatRetries)
                            {
                                string limitMessage = _getString(
                                    "AgentToolCallFormatRetryLimit",
                                    "도구 호출 형식 오류가 반복되어 Agent 실행을 중단했습니다. 작업을 다시 실행해 주세요.");
                                await _runOutputController.AppendRunActivityAsync(runContext, limitMessage);
                                await _runOutputController.AppendRunOutputLineAsync(runContext, limitMessage);

                                AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                                    runContext,
                                    conversationTurn,
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

                            if (makePlanRetryCount > maxMakePlanRetries)
                            {
                                string limitMessage = _getString(
                                    "AgentMakePlanRetryLimit",
                                    "make_plan 도구 호출이 생성되지 않아 계획 모드를 중단했습니다. 다시 실행해 주세요.");
                                await _runOutputController.AppendRunActivityAsync(runContext, limitMessage);
                                await _runOutputController.AppendRunOutputLineAsync(runContext, limitMessage);

                                AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                                    runContext,
                                    conversationTurn,
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
                            conversationTurn.Contains("[Enabled agent skills]", StringComparison.OrdinalIgnoreCase) &&
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
                                modelTranscript += retryDetail;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryDetail);
                                _updateContextStatsImmediate(true);
                            });

                            string retryMessage = _getString(
                                "AgentSkillNotCalledRetry",
                                "스킬을 호출하지 않고 설명만 했습니다. 도구 호출을 다시 시도합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
                            if (_runOutputController.IsSessionVisible(runContext.SessionId))
                            {
                                _updateContextStatsImmediate(true);
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
                            conversationTurn,
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
                                        var visibleSession = _openSessionController.EnsureSession(_currentSessionIdProvider());
                                        _sessionEditController.Replace(visibleSession.SessionEdits, _currentSessionIdProvider());
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
                        currentWorkspaceContext = _promptContextService.BuildWorkspaceContext(
                            conversationTurn,
                            currentRunActiveTab,
                            currentRunSelectionSnapshot,
                            runContext.Attachments,
                            runContext.WorkspaceRoot);
                        var addedPartBuilder = new StringBuilder();
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine("[assistant: tool call]");
                        addedPartBuilder.AppendLine(response);

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
                        transcript += addedPart;
                        modelTranscript += addedPart;
                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(addedPart);
                        _updateContextStatsImmediate(true);
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
                            conversationTurn,
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
                            conversationTurn,
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

                            string verificationPart = "\n\n[File edit verification: requested content already matches the current file. Task complete.]";
                            transcript += verificationPart;
                            modelTranscript += verificationPart;

                            await _runOutputController.AppendRunOutputLineAsync(runContext, completeMsg);

                            AgentRunTranscriptRecorder.AppendPromptTranscript(
                                runContext,
                                conversationTurn,
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
                        conversationTurn,
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
                    conversationTurn,
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
                _isRunning = _openSessionController.IsAnySessionRunning();
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
                    _restoreSessionHistoryState(
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
                    _updateContextStatsImmediate(false);
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

            context.Cancellation?.Cancel();
            _confirmationController.CancelPending();
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
