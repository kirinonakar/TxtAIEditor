using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentResponseStreamService
    {
        private readonly ILLMService _llmService;
        private readonly AgentPane _agentPane;
        private readonly AgentUiDispatcher _uiDispatcher;
        private readonly AgentRunOutputController _runOutputController;
        private readonly AgentOpenSessionController _openSessionController;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly AgentResponseInspector _responseInspector;
        private readonly Action<bool> _updateContextStatsImmediate;
        private readonly Func<string, string, string> _getString;

        public AgentResponseStreamService(
            ILLMService llmService,
            AgentPane agentPane,
            AgentUiDispatcher uiDispatcher,
            AgentRunOutputController runOutputController,
            AgentOpenSessionController openSessionController,
            AgentDisplayLocalizer displayText,
            AgentResponseInspector responseInspector,
            Action<bool> updateContextStatsImmediate,
            Func<string, string, string> getString)
        {
            _llmService = llmService;
            _agentPane = agentPane;
            _uiDispatcher = uiDispatcher;
            _runOutputController = runOutputController;
            _openSessionController = openSessionController;
            _displayText = displayText;
            _responseInspector = responseInspector;
            _updateContextStatsImmediate = updateContextStatsImmediate;
            _getString = getString;
        }

        public async Task<AgentResponseStreamResult> RunAsync(
            AgentRunContext runContext,
            string instruction,
            string currentTranscript,
            string runSelectionContext,
            bool planningMode,
            IReadOnlyList<LlmTool> agentToolsList,
            IReadOnlyList<LlmMessageAttachment> imageAttachments,
            CancellationToken cancellationToken)
        {
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

            bool inThoughtBlock = false;
            var thoughtTextBuilder = new StringBuilder();
            var cleanStreamTextBuilder = new StringBuilder();
            int rawProcessedLength = 0;

            var stepReasoningBuilder = new StringBuilder();
            Func<string, Task>? onReasoning = null;
            if (runContext.LlmSettings.LlmAgentVerbose)
            {
                onReasoning = async reasoningChunk =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stepReasoningBuilder.Append(reasoningChunk);
                    await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, reasoningChunk);
                };
            }
            else
            {
                onReasoning = async reasoningChunk =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stepReasoningBuilder.Append(reasoningChunk);
                    int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(stepReasoningBuilder.ToString()));
                    string label = string.Format(
                        _displayText.GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                        _getString("AgentActivityThinking", "생각중"),
                        _displayText.FormatInlineTokenCount(tokenCount)
                    );
                    _runOutputController.EnqueueRunUi(
                        runContext,
                        () => _agentPane.UpdateThinkingActivity(label),
                        session => _openSessionController.UpdateThinkingInSession(session, label));
                    await Task.CompletedTask;
                };
            }

            bool truncated = false;
            string response = string.Empty;
            try
            {
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

                                        string label = string.Format(
                                            _displayText.GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                                            _getString("AgentActivityThinking", "생각중"),
                                            _displayText.FormatInlineTokenCount(0)
                                        );
                                        _runOutputController.EnqueueRunUi(
                                            runContext,
                                            () => _agentPane.BeginThinkingActivity(label),
                                            session => _openSessionController.BeginThinkingInSession(session, label));
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

                                        int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(thoughtTextBuilder.ToString()));
                                        string label = string.Format(
                                            _displayText.GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                                            _getString("AgentActivityThinking", "생각중"),
                                            _displayText.FormatInlineTokenCount(tokenCount)
                                        );
                                        _runOutputController.EnqueueRunUi(
                                            runContext,
                                            () => _agentPane.UpdateThinkingActivity(label),
                                            session => _openSessionController.UpdateThinkingInSession(session, label));
                                    }
                                    else
                                    {
                                        thoughtTextBuilder.Append(rawStreamedText.Substring(currentPos, rawSafeLength - currentPos));
                                        currentPos = rawSafeLength;

                                        int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(thoughtTextBuilder.ToString()));
                                        string label = string.Format(
                                            _displayText.GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                                            _getString("AgentActivityThinking", "생각중"),
                                            _displayText.FormatInlineTokenCount(tokenCount)
                                        );
                                        _runOutputController.EnqueueRunUi(
                                            runContext,
                                            () => _agentPane.UpdateThinkingActivity(label),
                                            session => _openSessionController.UpdateThinkingInSession(session, label));
                                    }
                                }
                            }
                            rawProcessedLength = rawSafeLength;
                        }

                        string streamedText = runContext.LlmSettings.LlmAgentVerbose ? rawStreamedText : cleanStreamTextBuilder.ToString();

                        if (isJsonToolCall == null)
                        {
                            string trimmed = streamedText.TrimStart();
                            if (!string.IsNullOrEmpty(trimmed))
                            {
                                isJsonToolCall = _responseInspector.LooksLikeStreamedToolCallEnvelopeStart(trimmed);
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
                                bool toolCallClosed = streamedText.TrimEnd().EndsWith("}");
                                int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(streamedText));
                                string label;
                                if (toolCallClosed)
                                {
                                    label = string.Format(
                                        _displayText.GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                                        _getString("AgentActivityThinking", "생각중"),
                                        _displayText.FormatInlineTokenCount(tokenCount)
                                    );
                                }
                                else
                                {
                                    label = _displayText.FormatPreparingToolLabel(tokenCount);
                                }
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
                                int idx = AgentToolCallParser.FindToolCallIndex(streamedText);
                                int lastCloseIdx = AgentToolCallParser.FindLastToolCallCloseIndex(streamedText);
                                bool toolCallClosed = lastCloseIdx >= 0 && lastCloseIdx > idx;

                                string label;
                                if (toolCallClosed)
                                {
                                    int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(streamedText));
                                    label = string.Format(
                                        _displayText.GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                                        _getString("AgentActivityThinking", "생각중"),
                                        _displayText.FormatInlineTokenCount(tokenCount)
                                    );
                                }
                                else
                                {
                                    string toolCallText = idx >= 0 ? streamedText.Substring(idx) : streamedText;
                                    int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(toolCallText));
                                    label = _displayText.FormatPreparingToolLabel(tokenCount);
                                }

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

                        int toolCallIndex = AgentToolCallParser.FindToolCallIndex(streamedText);
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
                            string tag = "<tool_call";
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
                    imageAttachments,
                    planningMode,
                    onReasoning,
                    agentToolsList);
            }
            catch (ResponseTruncatedException)
            {
                truncated = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            response = responseBuilder.Length > 0 ? responseBuilder.ToString() : response;

            double stepReasoningTokens = AgentTokenEstimator.Estimate(stepReasoningBuilder.ToString());
            if (stepReasoningTokens > 0)
            {
                await _uiDispatcher.RunAsync(() =>
                {
                    runContext.CurrentRunTranscriptTokens += stepReasoningTokens;
                    _updateContextStatsImmediate(true);
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

                            int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(thoughtTextBuilder.ToString()));
                            string label = string.Format(
                                _displayText.GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                                _getString("AgentActivityThinking", "생각중"),
                                _displayText.FormatInlineTokenCount(tokenCount)
                            );
                            _runOutputController.EnqueueRunUi(
                                runContext,
                                () => _agentPane.UpdateThinkingActivity(label),
                                session => _openSessionController.UpdateThinkingInSession(session, label));
                        }
                        else
                        {
                            thoughtTextBuilder.Append(finalRawText.Substring(currentPos, finalSafeLength - currentPos));
                            currentPos = finalSafeLength;

                            int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(thoughtTextBuilder.ToString()));
                            string label = string.Format(
                                _displayText.GetString("AgentOutputPreparingToolWithTokensFormat", "{0} ({1})"),
                                _getString("AgentActivityThinking", "생각중"),
                                _displayText.FormatInlineTokenCount(tokenCount)
                            );
                            _runOutputController.EnqueueRunUi(
                                runContext,
                                () => _agentPane.UpdateThinkingActivity(label),
                                session => _openSessionController.UpdateThinkingInSession(session, label));
                        }
                    }
                }
            }

            return new AgentResponseStreamResult
            {
                Response = response,
                CleanResponse = runContext.LlmSettings.LlmAgentVerbose ? response : cleanStreamTextBuilder.ToString(),
                Truncated = truncated,
                PrintedLength = printedLength,
                HeldPotentialToolCallText = heldPotentialToolCallText,
                VisibleTextFlushed = visibleTextFlushed
            };
        }
    }

    internal sealed class AgentResponseStreamResult
    {
        public string Response { get; init; } = string.Empty;
        public string CleanResponse { get; init; } = string.Empty;
        public bool Truncated { get; init; }
        public int PrintedLength { get; init; }
        public bool HeldPotentialToolCallText { get; init; }
        public bool VisibleTextFlushed { get; init; }
    }
}
