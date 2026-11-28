using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentResponseStreamService
    {
        // Shared cadence for reasoning, answer text and tool-call streaming updates.
        private const long InFlightContextStatsIntervalMs = 500;

        private readonly ILLMService _llmService;
        private readonly AgentUiDispatcher _uiDispatcher;
        private readonly AgentRunOutputController _runOutputController;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly AgentResponseInspector _responseInspector;
        private readonly Action<bool> _updateContextStatsImmediate;
        private readonly Func<string, string, string> _getString;

        public AgentResponseStreamService(
            ILLMService llmService,
            AgentUiDispatcher uiDispatcher,
            AgentRunOutputController runOutputController,
            AgentDisplayLocalizer displayText,
            AgentResponseInspector responseInspector,
            Action<bool> updateContextStatsImmediate,
            Func<string, string, string> getString)
        {
            _llmService = llmService;
            _uiDispatcher = uiDispatcher;
            _runOutputController = runOutputController;
            _displayText = displayText;
            _responseInspector = responseInspector;
            _updateContextStatsImmediate = updateContextStatsImmediate;
            _getString = getString;
        }

        public async Task<AgentResponseStreamResult> RunAsync(
            AgentRunContext runContext,
            string fixedPromptContext,
            string currentTranscript,
            string currentWorkspaceContext,
            string runSelectionContext,
            bool planningMode,
            IReadOnlyList<LlmTool> agentToolsList,
            IReadOnlyList<LlmMessageAttachment> imageAttachments,
            CancellationToken cancellationToken,
            GeminiToolConversation? geminiConversation,
            bool deferAnswer = false)
        {
            if (geminiConversation != null)
            {
                geminiConversation.PendingTurn = null;
            }
            string thinkingLabel = _getString("AgentActivityThinking", "생각중");
            var requestThinking = new ThinkingProgress(_displayText);
            await _runOutputController.BeginRunThinkingActivityAsync(runContext, thinkingLabel, requestThinking.FormatLabel);

            var responseBuilder = new StringBuilder();
            runContext.StreamingResponseText = string.Empty;
            runContext.StreamingReasoningText = string.Empty;
            runContext.InFlightReasoningTokens = 0;
            runContext.InFlightResponseTokens = 0;
            // Estimate the captured request once off the UI thread. Streaming stats then
            // only add the in-flight token count instead of reading the editor/history again.
            int rawRequestEstimate = await Task.Run(
                () => AgentContextCompressionService.EstimateModelRequestTokens(
                    runContext.LlmSettings,
                    fixedPromptContext,
                    currentTranscript,
                    currentWorkspaceContext,
                    runSelectionContext,
                    planningMode,
                    runContext.HasEnabledSkills,
                    runContext.HasEnabledMcp,
                    agentToolsList,
                    imageAttachments),
                cancellationToken);
            runContext.ActualRequestTokens = runContext.RequestTokenCalibration.Estimate(rawRequestEstimate);
            runContext.ActualRequestTokensBase = runContext.CurrentRunTranscriptTokens;
            int printedLength = 0;
            bool visibleTextFlushed = false;
            bool heldPotentialToolCallText = false;
            bool? isJsonToolCall = null;
            bool jsonToolCallClosed = false;
            bool hasToolCall = false;
            bool nativeFunctionToolCallDetected = false;
            ThinkingProgress? nativeToolProgress = null;
            bool nativeToolActivityShown = false;
            double bufferedNativeToolTokens = 0;
            double streamedNativeToolTokens = 0;
            double countedNativeToolTokens = 0;
            var nativeToolDiagnostics = new StringBuilder();
            ThinkingProgress? textToolProgress = null;
            bool suppressStreamingText = planningMode || deferAnswer;

            bool inThoughtBlock = false;
            var thoughtTextBuilder = new StringBuilder();
            ThinkingProgress inlineThinking = requestThinking;
            var cleanStreamTextBuilder = new StringBuilder();
            int rawProcessedLength = 0;

            var stepReasoningBuilder = new StringBuilder();
            long lastInFlightStatsUpdateTicks = 0;

            async Task ShowNativeToolProgressAsync()
            {
                if (!nativeToolActivityShown)
                {
                    nativeToolProgress ??= new ThinkingProgress(_displayText);
                    await _runOutputController.BeginRunThinkingActivityAsync(
                        runContext, nativeToolProgress.FormatCallingToolLabel(), nativeToolProgress.FormatCallingToolLabel);
                    nativeToolActivityShown = true;
                }
            }

            void UpdateNativeToolTokens()
            {
                // A buffered call is emitted again as normalized tool text at the end of the stream.
                double tokens = Math.Max(bufferedNativeToolTokens, streamedNativeToolTokens);
                runContext.InFlightResponseTokens += tokens - countedNativeToolTokens;
                countedNativeToolTokens = tokens;
                nativeToolProgress ??= new ThinkingProgress(_displayText);
                nativeToolProgress.SetTokenCount(tokens);
            }

            Task AppendVisibleTextAsync(string text)
            {
                visibleTextFlushed = true;
                nativeToolActivityShown = false;
                return _runOutputController.AppendRunOutputTextAsync(runContext, text);
            }

            Task AppendDiagnosticTextAsync(string text)
            {
                if (nativeToolProgress == null)
                {
                    return _runOutputController.AppendRunDiagnosticTextAsync(runContext, text);
                }

                // Diagnostic output finishes the live activity line. Append it after preparation.
                nativeToolDiagnostics.Append(text);
                return Task.CompletedTask;
            }

            void UpdateInFlightReasoningTokens(string chunk)
            {
                // Keep the in-flight reasoning estimate in the run context so the panel
                // token count keeps growing while the model is thinking.
                runContext.InFlightReasoningTokens += AgentTokenEstimator.Estimate(chunk);
                requestThinking.SetTokenCount(runContext.InFlightReasoningTokens);
            }

            async Task RefreshContextStatsAsync(bool force = false)
            {
                long nowTicks = Environment.TickCount64;
                if (!force && nowTicks - lastInFlightStatsUpdateTicks < InFlightContextStatsIntervalMs)
                {
                    return;
                }

                lastInFlightStatsUpdateTicks = nowTicks;
                await _uiDispatcher.RunAsync(() =>
                {
                    if (_runOutputController.IsSessionVisible(runContext.SessionId))
                    {
                        _updateContextStatsImmediate(true);
                    }
                });
            }

            Func<string, Task>? onReasoning = null;
            if (runContext.LlmSettings.LlmAgentVerbose)
            {
                onReasoning = async reasoningChunk =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stepReasoningBuilder.Append(reasoningChunk);
                    runContext.StreamingReasoningText = stepReasoningBuilder.ToString();
                    UpdateInFlightReasoningTokens(reasoningChunk);
                    await AppendDiagnosticTextAsync(reasoningChunk);
                    await RefreshContextStatsAsync();
                };
            }
            else
            {
                onReasoning = async reasoningChunk =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stepReasoningBuilder.Append(reasoningChunk);
                    runContext.StreamingReasoningText = stepReasoningBuilder.ToString();
                    UpdateInFlightReasoningTokens(reasoningChunk);
                    await RefreshContextStatsAsync();
                };
            }

            int? observedPromptTokens = null;
            Func<LlmTokenUsage, Task> onUsage = async usage =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!runContext.VisionFallbackPending && usage.PromptTokens is > 0 &&
                    string.Equals(usage.Provider, runContext.LlmSettings.LlmProvider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(usage.Model, runContext.LlmSettings.LlmModel, StringComparison.OrdinalIgnoreCase))
                {
                    observedPromptTokens = usage.PromptTokens;
                }
                if (!runContext.LlmSettings.LlmAgentVerbose || !usage.HasAny)
                {
                    return;
                }

                string message = _displayText.FormatLlmTokenUsage(usage);
                if (nativeToolActivityShown)
                {
                    await _runOutputController.AppendRunNoticeAsync(runContext, message);
                }
                else
                {
                    await _runOutputController.AppendRunActivityAsync(runContext, message);
                }
            };

            bool truncated = false;
            string response = string.Empty;
            bool visionFallbackPending = runContext.VisionFallbackPending &&
                runContext.ImageToolAttachments.Count > 0;
            try
            {
                response = await _llmService.RunAgentAsync(
                    runContext.LlmSettings,
                    currentTranscript,
                    currentWorkspaceContext,
                    runSelectionContext,
                    "run",
                    async chunk =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        responseBuilder.Append(chunk);
                        runContext.StreamingResponseText = responseBuilder.ToString();
                        double chunkTokens = AgentTokenEstimator.Estimate(chunk);
                        if (nativeFunctionToolCallDetected)
                        {
                            streamedNativeToolTokens += chunkTokens;
                            UpdateNativeToolTokens();
                        }
                        else
                        {
                            runContext.InFlightResponseTokens += chunkTokens;
                        }
                        // Refresh before the filtering/tool-call branches, including their
                        // early returns. Hidden output still contributes to context length.
                        await RefreshContextStatsAsync();
                        string rawStreamedText = responseBuilder.ToString();

                        if (!runContext.LlmSettings.LlmAgentVerbose)
                        {
                            int holdBack = 0;
                            string[] tagsToHold = {
                                "<think>", "<thought>", "<|channel>thought",
                                "</think>", "</thought>", "<channel|>",
                                "<tool_call"
                            };
                            foreach (var tag in tagsToHold)
                            {
                                for (int i = 1; i < tag.Length; i++)
                                {
                                    string sub = tag.Substring(0, i);
                                    if (rawStreamedText.EndsWith(sub, StringComparison.OrdinalIgnoreCase))
                                    {
                                        holdBack = Math.Max(holdBack, i);
                                        break;
                                    }
                                }
                            }
                            int rawSafeLength = rawStreamedText.Length - holdBack;

                            string[] thinkStartTags = { "<think>", "<thought>", "<|channel>thought" };
                            string[] thinkEndTags = { "</think>", "</thought>", "<channel|>" };

                            int currentPos = rawProcessedLength;
                            while (currentPos < rawSafeLength)
                            {
                                if (!inThoughtBlock)
                                {
                                    int earliestStartIdx = -1;
                                    string matchedStartTag = "";
                                    foreach (var tag in thinkStartTags)
                                    {
                                        int idx = rawStreamedText.IndexOf(tag, currentPos, StringComparison.OrdinalIgnoreCase);
                                        if (idx >= 0 && idx < rawSafeLength)
                                        {
                                            if (earliestStartIdx == -1 || idx < earliestStartIdx)
                                            {
                                                earliestStartIdx = idx;
                                                matchedStartTag = tag;
                                            }
                                        }
                                    }

                                    if (earliestStartIdx >= 0)
                                    {
                                        if (earliestStartIdx > currentPos)
                                        {
                                            cleanStreamTextBuilder.Append(rawStreamedText.Substring(currentPos, earliestStartIdx - currentPos));
                                        }
                                        inThoughtBlock = true;
                                        thoughtTextBuilder.Clear();
                                        currentPos = earliestStartIdx + matchedStartTag.Length;

                                        if (nativeToolProgress == null && (visibleTextFlushed ||
                                            !rawStreamedText.AsSpan(0, earliestStartIdx).IsWhiteSpace() ||
                                            runContext.InFlightReasoningTokens > 0))
                                        {
                                            inlineThinking = new ThinkingProgress(_displayText);
                                            await _runOutputController.BeginRunThinkingActivityAsync(
                                                runContext, thinkingLabel, inlineThinking.FormatLabel);
                                        }
                                    }
                                    else
                                    {
                                        cleanStreamTextBuilder.Append(rawStreamedText.Substring(currentPos, rawSafeLength - currentPos));
                                        currentPos = rawSafeLength;
                                    }
                                }
                                else
                                {
                                    int earliestEndIdx = -1;
                                    string matchedEndTag = "";
                                    foreach (var tag in thinkEndTags)
                                    {
                                        int idx = rawStreamedText.IndexOf(tag, currentPos, StringComparison.OrdinalIgnoreCase);
                                        if (idx >= 0 && idx < rawSafeLength)
                                        {
                                            if (earliestEndIdx == -1 || idx < earliestEndIdx)
                                            {
                                                earliestEndIdx = idx;
                                                matchedEndTag = tag;
                                            }
                                        }
                                    }

                                    if (earliestEndIdx >= 0)
                                    {
                                        if (earliestEndIdx > currentPos)
                                        {
                                            thoughtTextBuilder.Append(rawStreamedText.Substring(currentPos, earliestEndIdx - currentPos));
                                        }
                                        inThoughtBlock = false;
                                        currentPos = earliestEndIdx + matchedEndTag.Length;

                                        inlineThinking.SetTokenCount(AgentTokenEstimator.Estimate(thoughtTextBuilder.ToString()));
                                    }
                                    else
                                    {
                                        thoughtTextBuilder.Append(rawStreamedText.Substring(currentPos, rawSafeLength - currentPos));
                                        currentPos = rawSafeLength;

                                        inlineThinking.SetTokenCount(AgentTokenEstimator.Estimate(thoughtTextBuilder.ToString()));
                                    }
                                }
                            }
                            rawProcessedLength = rawSafeLength;
                        }

                        string streamedText = runContext.LlmSettings.LlmAgentVerbose
                            ? rawStreamedText
                            : AgentNonVerboseTextFilter.Filter(cleanStreamTextBuilder.ToString());

                        if (isJsonToolCall == null && !nativeFunctionToolCallDetected)
                        {
                            string trimmed = streamedText.TrimStart();
                            if (!string.IsNullOrEmpty(trimmed))
                            {
                                isJsonToolCall = _responseInspector.LooksLikeStreamedToolCallEnvelopeStart(trimmed);
                            }
                        }

                        if (isJsonToolCall == true && !nativeFunctionToolCallDetected)
                        {
                            if (!runContext.LlmSettings.LlmAgentVerbose)
                            {
                                double tokens = AgentTokenEstimator.Estimate(streamedText);
                                Volatile.Write(ref jsonToolCallClosed, streamedText.TrimEnd().EndsWith('}'));
                                if (textToolProgress == null)
                                {
                                    heldPotentialToolCallText = true;
                                    var progress = new ThinkingProgress(_displayText);
                                    textToolProgress = progress;
                                    progress.SetTokenCount(tokens);
                                    string LiveJsonLabel() => Volatile.Read(ref jsonToolCallClosed)
                                        ? _displayText.FormatThinkingLabel(progress.TokenCount, requestThinking.Elapsed)
                                        : progress.FormatCallingToolLabel();
                                    await _runOutputController.BeginRunThinkingActivityAsync(runContext, LiveJsonLabel(), LiveJsonLabel);
                                }
                                textToolProgress.SetTokenCount(tokens);
                            }
                            else
                            {
                                await AppendDiagnosticTextAsync(streamedText.Substring(printedLength));
                            }
                            printedLength = streamedText.Length;
                            return;
                        }

                        if (hasToolCall)
                        {
                            if (runContext.LlmSettings.LlmAgentVerbose)
                            {
                                await AppendDiagnosticTextAsync(chunk);
                                printedLength = streamedText.Length;
                            }
                            else if (!nativeFunctionToolCallDetected && textToolProgress != null)
                            {
                                int idx = AgentToolCallParser.FindToolCallIndex(streamedText);
                                string toolCallText = idx >= 0 ? streamedText.Substring(idx) : streamedText;
                                textToolProgress.SetTokenCount(AgentTokenEstimator.Estimate(toolCallText));
                            }
                            return;
                        }

                        int toolCallIndex = AgentToolCallParser.FindToolCallIndex(streamedText);
                        if (toolCallIndex >= 0)
                        {
                            hasToolCall = true;
                            if (printedLength < toolCallIndex)
                            {
                                string textToPrint = streamedText.Substring(printedLength, toolCallIndex - printedLength);
                                if (!suppressStreamingText &&
                                    (!nativeToolActivityShown || !string.IsNullOrWhiteSpace(textToPrint)))
                                {
                                    await AppendVisibleTextAsync(textToPrint);
                                }
                                printedLength = toolCallIndex;
                            }

                            if (nativeFunctionToolCallDetected)
                            {
                                await ShowNativeToolProgressAsync();
                            }

                            if (runContext.LlmSettings.LlmAgentVerbose)
                            {
                                await AppendDiagnosticTextAsync(streamedText.Substring(toolCallIndex));
                                printedLength = streamedText.Length;
                            }
                            else if (!nativeFunctionToolCallDetected && textToolProgress == null)
                            {
                                textToolProgress = new ThinkingProgress(_displayText);
                                textToolProgress.SetTokenCount(AgentTokenEstimator.Estimate(streamedText.Substring(toolCallIndex)));
                                await _runOutputController.BeginRunThinkingActivityAsync(
                                    runContext, textToolProgress.FormatCallingToolLabel(), textToolProgress.FormatCallingToolLabel);
                            }
                        }
                        else
                        {
                            int holdBack = AgentToolCallParser.GetPotentialToolCallTagSuffixLength(streamedText);
                            int safeLength = streamedText.Length - holdBack;
                            if (printedLength < safeLength)
                            {
                                string textToPrint = streamedText.Substring(printedLength, safeLength - printedLength);
                                if (!suppressStreamingText)
                                {
                                    await AppendVisibleTextAsync(textToPrint);
                                }
                                printedLength = safeLength;
                            }
                        }
                        if (bufferedNativeToolTokens > 0)
                        {
                            await ShowNativeToolProgressAsync();
                        }
                    },
                    cancellationToken,
                    imageAttachments,
                    planningMode,
                    onReasoning,
                    agentToolsList,
                    runContext.HasEnabledSkills,
                    runContext.HasEnabledMcp,
                    onUsage,
                    allowVisionFallback: visionFallbackPending,
                    onVisionFallbackResult: fallbackContext =>
                    {
                        if (AgentToolCallParser.ContainsToolCallSyntax(fallbackContext))
                        {
                            return Task.FromResult(true);
                        }

                        runContext.PendingVisionFallbackContext = fallbackContext;
                        return Task.FromResult(false);
                    },
                    fixedContext: fixedPromptContext,
                    onNativeToolCall: async () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        nativeFunctionToolCallDetected = true;
                        await ShowNativeToolProgressAsync();
                    },
                    onApiType: apiType =>
                    {
                        runContext.ApiType = apiType;
                        return Task.CompletedTask;
                    },
                    sessionId: runContext.SessionId,
                    geminiConversation: geminiConversation,
                    onBufferedNativeToolCall: async call =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bufferedNativeToolTokens += AgentTokenEstimator.Estimate(call);
                        UpdateNativeToolTokens();
                        await ShowNativeToolProgressAsync();
                        await RefreshContextStatsAsync();
                    });
                if (nativeFunctionToolCallDetected && streamedNativeToolTokens == 0)
                {
                    int toolCallIndex = AgentToolCallParser.FindToolCallIndex(response);
                    if (toolCallIndex >= 0)
                    {
                        streamedNativeToolTokens = AgentTokenEstimator.Estimate(response.Substring(toolCallIndex));
                        UpdateNativeToolTokens();
                    }
                }
                await RefreshContextStatsAsync(force: true);
            }
            catch (ResponseTruncatedException)
            {
                truncated = true;
                await RefreshContextStatsAsync(force: true);
            }
            finally
            {
                if (nativeToolDiagnostics.Length > 0)
                {
                    await _runOutputController.AppendRunDiagnosticTextAsync(runContext, nativeToolDiagnostics.ToString());
                }
                runContext.InFlightReasoningTokens = 0;
                runContext.InFlightResponseTokens = 0;
                if (visionFallbackPending)
                {
                    runContext.VisionFallbackPending = false;
                    runContext.ImageToolAttachments.Clear();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (observedPromptTokens is > 0)
            {
                runContext.RequestTokenCalibration.Record(rawRequestEstimate, observedPromptTokens.Value);
                runContext.ActualRequestTokens = observedPromptTokens.Value;
            }
            response = responseBuilder.Length > 0 ? responseBuilder.ToString() : response;
            if (responseBuilder.Length == 0 &&
                stepReasoningBuilder.Length > 0 &&
                string.Equals(response, stepReasoningBuilder.ToString(), StringComparison.Ordinal))
            {
                response = string.Empty;
            }

            double stepReasoningTokens = AgentTokenEstimator.Estimate(stepReasoningBuilder.ToString());
            if (stepReasoningTokens > 0 && !runContext.LlmSettings.LlmRetainThinking)
            {
                await _uiDispatcher.RunAsync(() =>
                {
                    runContext.CurrentRunTranscriptTokens += stepReasoningTokens;
                    // The final streaming refresh already includes both reasoning and
                    // response tokens. Refresh again after the coordinator commits the
                    // response, so clearing the overlays does not briefly drop the count.
                });
            }

            if (!runContext.LlmSettings.LlmAgentVerbose && responseBuilder.Length > 0)
            {
                string finalRawText = responseBuilder.ToString();
                int finalSafeLength = finalRawText.Length;

                string[] thinkStartTags = { "<think>", "<thought>", "<|channel>thought" };
                string[] thinkEndTags = { "</think>", "</thought>", "<channel|>" };

                int currentPos = rawProcessedLength;
                while (currentPos < finalSafeLength)
                {
                    if (!inThoughtBlock)
                    {
                        int earliestStartIdx = -1;
                        string matchedStartTag = "";
                        foreach (var tag in thinkStartTags)
                        {
                            int idx = finalRawText.IndexOf(tag, currentPos, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0 && idx < finalSafeLength)
                            {
                                if (earliestStartIdx == -1 || idx < earliestStartIdx)
                                {
                                    earliestStartIdx = idx;
                                    matchedStartTag = tag;
                                }
                            }
                        }

                        if (earliestStartIdx >= 0)
                        {
                            if (earliestStartIdx > currentPos)
                            {
                                cleanStreamTextBuilder.Append(finalRawText.Substring(currentPos, earliestStartIdx - currentPos));
                            }
                            inThoughtBlock = true;
                            thoughtTextBuilder.Clear();
                            currentPos = earliestStartIdx + matchedStartTag.Length;
                        }
                        else
                        {
                            cleanStreamTextBuilder.Append(finalRawText.Substring(currentPos, finalSafeLength - currentPos));
                            currentPos = finalSafeLength;
                        }
                    }
                    else
                    {
                        int earliestEndIdx = -1;
                        string matchedEndTag = "";
                        foreach (var tag in thinkEndTags)
                        {
                            int idx = finalRawText.IndexOf(tag, currentPos, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0 && idx < finalSafeLength)
                            {
                                if (earliestEndIdx == -1 || idx < earliestEndIdx)
                                {
                                    earliestEndIdx = idx;
                                    matchedEndTag = tag;
                                }
                            }
                        }

                        if (earliestEndIdx >= 0)
                        {
                            if (earliestEndIdx > currentPos)
                            {
                                thoughtTextBuilder.Append(finalRawText.Substring(currentPos, earliestEndIdx - currentPos));
                            }
                            inThoughtBlock = false;
                            currentPos = earliestEndIdx + matchedEndTag.Length;

                            inlineThinking.SetTokenCount(AgentTokenEstimator.Estimate(thoughtTextBuilder.ToString()));
                        }
                        else
                        {
                            thoughtTextBuilder.Append(finalRawText.Substring(currentPos, finalSafeLength - currentPos));
                            currentPos = finalSafeLength;

                            inlineThinking.SetTokenCount(AgentTokenEstimator.Estimate(thoughtTextBuilder.ToString()));
                        }
                    }
                }
            }

            string responseWithoutThinking = AgentThinkingTranscriptFormatter.RemoveInlineThinking(response);
            string thinkingText = AgentThinkingTranscriptFormatter.CollectThinking(
                stepReasoningBuilder.ToString(),
                response);

            return new AgentResponseStreamResult
            {
                Response = response,
                CleanResponse = runContext.LlmSettings.LlmAgentVerbose
                    ? response
                    : AgentNonVerboseTextFilter.Filter(cleanStreamTextBuilder.ToString(), isComplete: true),
                ResponseWithoutThinking = responseWithoutThinking,
                ThinkingText = thinkingText,
                Truncated = truncated,
                PrintedLength = printedLength,
                HeldPotentialToolCallText = heldPotentialToolCallText,
                VisibleTextFlushed = visibleTextFlushed,
                UsedNativeFunctionToolCall = nativeFunctionToolCallDetected,
                GeminiTurn = geminiConversation?.PendingTurn
            };
        }

        private sealed class ThinkingProgress(AgentDisplayLocalizer displayText)
        {
            private readonly long _startedAt = Stopwatch.GetTimestamp();
            private double _estimatedTokens;

            public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startedAt);
            public double TokenCount => Volatile.Read(ref _estimatedTokens);

            public void SetTokenCount(double estimatedTokens) => Volatile.Write(ref _estimatedTokens, estimatedTokens);

            public string FormatLabel() => displayText.FormatThinkingLabel(TokenCount, Elapsed);

            public string FormatCallingToolLabel() => displayText.FormatCallingToolLabel(TokenCount, Elapsed);
        }
    }

    internal sealed class AgentResponseStreamResult
    {
        public string Response { get; init; } = string.Empty;
        public string CleanResponse { get; init; } = string.Empty;
        public string ResponseWithoutThinking { get; init; } = string.Empty;
        public string ThinkingText { get; init; } = string.Empty;
        public bool Truncated { get; init; }
        public int PrintedLength { get; init; }
        public bool HeldPotentialToolCallText { get; init; }
        public bool VisibleTextFlushed { get; init; }
        public bool UsedNativeFunctionToolCall { get; init; }
        public GeminiFunctionTurn? GeminiTurn { get; init; }
    }
}
