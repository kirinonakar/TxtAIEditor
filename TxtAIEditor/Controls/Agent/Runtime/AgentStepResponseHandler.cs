using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal abstract record AgentStepResponse;

    internal sealed record AgentFinalAnswer(string TranscriptLog) : AgentStepResponse;

    internal sealed record AgentToolTurn(
        IReadOnlyList<AgentToolCallParser.ToolCallInfo> Calls,
        string ResponseForTranscript,
        string RetainedThinkingSection,
        bool UsedNativeFunctionToolCall) : AgentStepResponse;

    internal sealed class AgentStepResponseHandler(
        AgentResponseInspector responseInspector,
        AgentRunTranscriptService runTranscriptService,
        AgentRunOutputController runOutputController,
        AgentUiDispatcher uiDispatcher,
        Func<string, string, string> getString,
        Action<bool> updateContextStatsImmediate)
    {
        public async Task<AgentStepResponse?> HandleAsync(
            AgentRunContext runContext,
            AgentResponseStreamResult streamResult,
            AgentRunTranscript transcript,
            AgentStepRetryPolicy retries,
            AgentSubagentCoordinator? orchestration,
            Task followUpRequested,
            bool isSubagent,
            bool deferAnswer)
        {
            bool planningMode = runContext.IsPlanningMode;
            string response = streamResult.Response;
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
                await runOutputController.StopRunThinkingActivityAsync(runContext);

                if (retries.TryRetry(AgentRetryReason.Truncation))
                {
                    string continuationNote =
                        "\n\n[Agent response truncated]\n" +
                        "Your previous response was cut off due to the output token limit. " +
                        "Continue exactly from where you stopped. Do not repeat what you already wrote. " +
                        "If you were about to write a tool_call, write it now.";
                    string retryDetail = runTranscriptService.BuildRetryDetail(
                        "truncated_response",
                        responseForTranscript,
                        continuationNote);
                    retryDetail = retainedThinkingTranscriptPart + retryDetail;

                    await uiDispatcher.RunAsync(() =>
                    {
                        transcript.AppendWithTokenCount(retryDetail);
                        updateContextStatsImmediate(true);
                    });
                    string truncationRetryMessage = getString(
                        "AgentActivityTruncatedRetry",
                        "응답이 잘려 이어서 작성합니다.");
                    await runOutputController.AppendRunActivityAsync(runContext, truncationRetryMessage);
                    return null;
                }
                throw new LlmRequestException(getString("AgentTruncatedRetryLimit", "응답 잘림이 반복되어 Agent 실행을 중단했습니다. 다시 실행해 주세요."));
            }

            GeminiFunctionTurn? geminiTurn = streamResult.GeminiTurn;
            if (geminiTurn == null && string.IsNullOrWhiteSpace(responseForTranscript))
            {
                await runOutputController.StopRunThinkingActivityAsync(runContext);

                if (retries.TryRetry(AgentRetryReason.EmptyResponse))
                {
                    string retryNote =
                        "\n\n[Agent empty response]\n" +
                        "The model returned no visible content. Continue with a final tool_call or a final answer.";
                    string retryDetail = runTranscriptService.BuildRetryDetail(
                        "empty_response",
                        string.Empty,
                        retryNote);
                    retryDetail = retainedThinkingTranscriptPart + retryDetail;

                    await uiDispatcher.RunAsync(() =>
                    {
                        transcript.AppendWithTokenCount(retryDetail);
                        updateContextStatsImmediate(true);
                    });
                    string emptyRetryMessage = getString(
                        "AgentActivityEmptyResponseRetry",
                        "빈 응답을 수신해 다시 시도합니다.");
                    await runOutputController.AppendRunActivityAsync(runContext, emptyRetryMessage);

                    return null;
                }

                string emptyResponseMessage = getString(
                    "LlmErrorEmptyResponse",
                    "AI로부터 빈 응답을 수신했습니다.");

                await runOutputController.AppendRunActivityAsync(runContext, emptyResponseMessage);
                await runOutputController.AppendRunOutputLineAsync(runContext, emptyResponseMessage);

                throw new LlmRequestException(emptyResponseMessage);
            }

            await runOutputController.StopRunThinkingActivityAsync(runContext);

            bool responseLooksLikeToolResultReplay = geminiTurn == null && responseInspector.LooksLikeToolResultReplay(response);
            bool responseHasToolSyntax = geminiTurn != null || AgentToolCallParser.ContainsToolCallSyntax(response);
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

            if (responseHasToolSyntax && !responseLooksLikeToolResultReplay &&
                (planningMode || deferAnswer || !visibleTextFlushed))
            {
                int toolCallIndex = AgentToolCallParser.FindToolCallIndex(cleanResponse);
                if (toolCallIndex > 0)
                {
                    string visiblePrefix = cleanResponse.Substring(0, toolCallIndex);
                    if (!string.IsNullOrWhiteSpace(visiblePrefix))
                    {
                        visibleTextFlushed = true;
                        await runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, visiblePrefix);
                        await runOutputController.EndStreamedAnswerAsync(runContext);
                    }
                }
            }
            else if (!planningMode && !deferAnswer && !responseRequiresToolHandling && heldPotentialToolCallText && !visibleTextFlushed && !string.IsNullOrEmpty(cleanResponse))
            {
                visibleTextFlushed = true;
                await runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, cleanResponse);
            }
            else if (!planningMode && !deferAnswer && printedLength < endLength)
            {
                string remainingText = cleanResponse.Substring(printedLength, endLength - printedLength);
                visibleTextFlushed = true;
                await runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, remainingText);
            }

            runContext.ToolCancellationToken.ThrowIfCancellationRequested();

            if (geminiTurn != null)
            {
                var nativeCalls = new List<AgentToolCallParser.ToolCallInfo>();
                for (int index = 0; index < geminiTurn.Calls.Count; index++)
                {
                    var call = geminiTurn.Calls[index];
                    if (call.ToolName != null)
                    {
                        nativeCalls.Add(new AgentToolCallParser.ToolCallInfo
                        {
                            ToolName = call.ToolName,
                            Arguments = call.Arguments,
                            NativeCallIndex = index
                        });
                    }
                }
                return new AgentToolTurn(nativeCalls, responseForTranscript, retainedThinkingSection, true);
            }

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
                    string retryNote = responseInspector.BuildToolCallFormatRetryNote(
                        !string.IsNullOrWhiteSpace(toolCallFormatIssue)
                            ? toolCallFormatIssue
                            : "The tool_call JSON could not be parsed.");
                    string retryDetail = runTranscriptService.BuildRetryDetail(
                        responseLooksLikeToolResultReplay ? "tool_result_replay" : "tool_call_format",
                        responseForTranscript,
                        retryNote);
                    runContext.RetryDebugHistory.AppendLine(retryDetail);
                    string retryPromptContext =
                        retainedThinkingTranscriptPart +
                        "\n\n[Retry instruction]\n" +
                        retryNote.Trim();
                    transcript.AppendWithTokenCount(retryPromptContext);

                    string retryMessage = getString(
                        "AgentToolCallFormatRetry",
                        "도구 호출을 해석하지 못해 다시 요청합니다.");
                    await runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                    await runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
                    if (runOutputController.IsSessionVisible(runContext.SessionId))
                    {
                        updateContextStatsImmediate(true);
                    }

                    if (!retryAllowed)
                    {
                        string limitMessage = getString(
                            "AgentToolCallFormatRetryLimit",
                            "도구 호출 형식 오류가 반복되어 Agent 실행을 중단했습니다. 작업을 다시 실행해 주세요.");
                        await runOutputController.AppendRunActivityAsync(runContext, limitMessage);
                        await runOutputController.AppendRunOutputLineAsync(runContext, limitMessage);

                        throw new LlmRequestException(limitMessage);
                    }

                    return null;
                }

                if (orchestration?.NeedsModelReview == true)
                {
                    await orchestration.WaitForEventAsync(followUpRequested);
                    return null;
                }

                if (isSubagent && !runContext.FollowUps.TryCloseIfEmpty())
                {
                    return null;
                }

                if (planningMode)
                {
                    bool retryAllowed = retries.TryRetry(AgentRetryReason.MakePlan);
                    string retryNote = AgentPlanController.BuildMakePlanRequiredRetryNote(runContext);
                    string retryDetail = runTranscriptService.BuildRetryDetail(
                        "make_plan_required",
                        responseForTranscript,
                        retryNote);
                    retryDetail = retainedThinkingTranscriptPart + retryDetail;

                    transcript.AppendWithTokenCount(retryDetail);

                    string retryMessage = getString(
                        "AgentMakePlanRequired",
                        "계획 모드에서는 make_plan 도구로 계획서를 저장해야 합니다.");
                    await runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                    await runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
                    if (runOutputController.IsSessionVisible(runContext.SessionId))
                    {
                        updateContextStatsImmediate(true);
                    }

                    if (!retryAllowed)
                    {
                        string limitMessage = getString(
                            "AgentMakePlanRetryLimit",
                            "make_plan 도구 호출이 생성되지 않아 계획 모드를 중단했습니다. 다시 실행해 주세요.");
                        await runOutputController.AppendRunActivityAsync(runContext, limitMessage);
                        await runOutputController.AppendRunOutputLineAsync(runContext, limitMessage);

                        throw new LlmRequestException(limitMessage);
                    }

                    return null;
                }

                if (!visibleTextFlushed && !string.IsNullOrWhiteSpace(response))
                {
                    await runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, response);
                }

                retries.Reset(AgentRetryReason.ToolCallFormat);
                runContext.LastAnswerText = AgentRunTextFormatter.BuildLastAnswerText(response, cleanResponse, runContext.LlmSettings.LlmAgentVerbose);
                await runOutputController.AppendRunActivityAsync(runContext, getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                if (runContext.StreamToTab)
                {
                    await runOutputController.EndStreamedAnswerAsync(runContext);
                }

                string finalAnswerLog = AgentThinkingTranscriptFormatter.BuildFinalAnswerLog(
                    responseForTranscript,
                    retainedThinking);
                return new AgentFinalAnswer(finalAnswerLog);
            }

            return new AgentToolTurn(toolCalls, responseForTranscript, retainedThinkingSection, usedNativeFunctionToolCall);
        }
    }
}
