using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentModelRequest(
        string Transcript,
        IReadOnlyList<LlmTool> Tools,
        IReadOnlyList<LlmMessageAttachment> ImageAttachments);

    internal sealed class AgentModelRequestBuilder(
        AgentUiDispatcher uiDispatcher,
        AgentMcpController mcpController,
        AgentSkillController skillController,
        AgentPromptContextService promptContextService,
        AgentLlmToolCatalog llmToolCatalog,
        AgentContextCompressionService contextCompressionService,
        AgentRunTranscriptService runTranscriptService,
        AgentRunOutputController runOutputController,
    Func<bool> isChatTabOpen,
        Func<string, string, string> getString,
        Action<bool> updateContextStatsImmediate)
    {
        public async Task<string> BuildFixedPromptContextAsync(
            AgentRunContext runContext, AgentSubagentCoordinator? orchestration, bool isSubagent)
        {
            bool planningMode = runContext.IsPlanningMode;
            CancellationToken cancellationToken = runContext.CancellationToken;
            string fixedPromptContext = await uiDispatcher.RunAsync<string>(async () =>
            {
                using var selectionScope = mcpController.UseSelections(runContext.Selections);
                await mcpController.ApplyPendingPluginChangesAsync(cancellationToken);
                AgentWorkspaceFileResolver.VerifyPinnedWorkspace(runContext.WorkspaceRoot, runContext.PinnedWorkspaceRoot);
                runContext.HasEnabledSkills = skillController.HasSelectedSkills();
                runContext.HasEnabledMcp = !runContext.IsPlanResearch && mcpController.HasSelectedMcpServers();
                return promptContextService.BuildFixedPromptContextForRun(
                    runContext.PinnedWorkspaceRoot, includeMcp: !runContext.IsPlanResearch,
                    settings: runContext.LlmSettings, planningMode: planningMode,
                    includeChatRendering: !isSubagent && runOutputController.IsSessionVisible(runContext.SessionId) && isChatTabOpen());
            });
            if (orchestration != null)
            {
                fixedPromptContext += "\n\n" + orchestration.BuildInstructions(planningMode);
            }
            else if (isSubagent)
            {
                fixedPromptContext += "\n\n" + AgentSubagentCoordinator.BuildWorkerInstructions(runContext.IsPlanResearch);
            }

            return fixedPromptContext;
        }

        public async Task<AgentModelRequest> BuildAsync(
            AgentRunContext runContext,
            AgentRunTranscript transcript,
            int taskStartEditIndex,
            string fixedPromptContext,
            string currentWorkspaceContext,
            string runSelectionContext,
            AgentSubagentCoordinator? orchestration,
            bool isSubagent)
        {
            bool planningMode = runContext.IsPlanningMode;
            CancellationToken cancellationToken = runContext.CancellationToken;

            string TakeEventBatch()
            {
                string events = orchestration?.DrainModelEvents() ?? string.Empty;
                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(events);
                return events;
            }

            string eventBatch = TakeEventBatch();
            string currentTranscript = runTranscriptService.BuildWithEditLedger(
                transcript.ModelText + eventBatch,
                taskStartEditIndex,
                runContext.SessionEdits);
            IReadOnlyList<LlmTool> agentTools = llmToolCatalog.Build(
                planningMode,
                runContext.IsPlanResearch ? Array.Empty<AgentMcpToolAlias>() : mcpController.GetActiveToolAliases(),
                runContext.HasEnabledSkills);
            if (orchestration != null)
            {
                agentTools = agentTools.Concat(AgentSubagentCoordinator.Tools).ToArray();
            }
            else if (isSubagent)
            {
                agentTools = agentTools
                    .Where(tool => AgentSubagentCoordinator.IsWorkerToolAvailable(tool.Name, runContext.IsPlanResearch))
                    .Append(AgentSubagentCoordinator.ProgressTool).ToArray();
            }
            IReadOnlyList<LlmMessageAttachment> imageAttachments =
                promptContextService.GetImageAttachmentsForRun(runContext);
            int EstimateCurrentRequestTokens() => AgentContextCompressionService.EstimateModelRequestTokens(
                runContext.LlmSettings, fixedPromptContext, currentTranscript, currentWorkspaceContext,
                runSelectionContext, planningMode, runContext.HasEnabledSkills, runContext.HasEnabledMcp,
                agentTools, imageAttachments);

            bool compressed = false;
            int requestTokens = 0;
            for (int pass = 0; ; pass++)
            {
                AgentContextCompressionResult compressionResult = await contextCompressionService.CompressIfNeededAsync(
                    runContext.LlmSettings,
                    fixedPromptContext,
                    transcript.ModelText,
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
                    transcript.GeminiTools?.ReplaceTranscriptPrefix(compressionResult.ReplacedPrefixLength, compressionResult.NewPrefixLength);
                    transcript.ModelText = compressionResult.Transcript;
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
                    currentTranscript = runTranscriptService.BuildWithEditLedger(
                        transcript.ModelText + eventBatch, taskStartEditIndex, runContext.SessionEdits);
                }

                if (orchestration == null && !compressed)
                {
                    break;
                }

                requestTokens = EstimateCurrentRequestTokens();
                if (orchestration == null || !contextCompressionService.ExceedsInputBudget(
                    runContext.LlmSettings, requestTokens, runContext.RequestTokenCalibration))
                {
                    break;
                }

                if (pass >= 1 || (!compressionResult.Compressed && !appendedLateEvents))
                {
                    transcript.ModelText += eventBatch;
                    throw new LlmRequestException(getString("AgentOrchestratorContextBudgetExceeded",
                        "압축 후에도 오케스트레이터의 입력이 모델 컨텍스트 한도를 초과합니다."));
                }
            }

            transcript.ModelText += eventBatch;
            if (compressed)
            {
                // The panel token display must show the compressed request that is
                // actually sent to the model, not the pre-compression transcript size.
                runContext.ActualRequestTokens = requestTokens;
                runContext.ActualRequestTokensBase = runContext.CurrentRunTranscriptTokens;
                string compressionNotice = getString(
                    "AgentContextCompressedNotice",
                    "context 압축이 시행되었습니다.");
                transcript.DisplayText +=
                    Environment.NewLine + Environment.NewLine +
                    compressionNotice +
                    Environment.NewLine + Environment.NewLine +
                    AgentRunTranscriptService.CompressedContextStartMarker +
                    Environment.NewLine +
                    transcript.ModelText.TrimEnd() +
                    Environment.NewLine +
                    AgentRunTranscriptService.CompressedContextEndMarker;
                await runOutputController.AppendRunOutputLineAsync(
                    runContext,
                    compressionNotice);
                await uiDispatcher.RunAsync(() => updateContextStatsImmediate(true));
            }

            return new AgentModelRequest(currentTranscript, agentTools, imageAttachments);
        }
    }
}
