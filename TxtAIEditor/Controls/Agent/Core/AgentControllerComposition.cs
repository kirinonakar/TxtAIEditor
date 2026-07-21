using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentControllerCompositionCallbacks(
        Func<bool> IsCurrentSessionRunning,
        Action UpdateContextStats,
        Action UpdateContextStatsSlow,
        Action<bool> UpdateContextStatsImmediate,
        Func<string?> GetActiveRunWorkspaceRoot,
        Func<OpenedTab?> GetActiveTabForContext,
        Func<string> GetActiveSelectionText,
        Func<string> BuildActiveSelectionContext,
        Func<AgentSelectionSnapshot> CaptureActiveSelectionSnapshot,
        Func<EditorSettings> GetCurrentSessionSettings,
        Func<double> GetCurrentRunTranscriptTokens,
        Action<string, double, double> RestoreSessionHistoryState,
        Action<string> StopAgent,
        Action<LlmMessageAttachment> AddCurrentRunImageToolAttachment,
        Action<string> AppendActivity,
        Func<AgentRunContext?> GetActiveRunContext);

    internal sealed record AgentControllerComposition(
        AgentUiDispatcher UiDispatcher,
        AgentDisplayLocalizer DisplayText,
        AgentAttachmentController AttachmentController,
        AgentPresetController PresetController,
        AgentSkillController SkillController,
        AgentMcpController McpController,
        AgentHistoryController HistoryController,
        AgentSessionEditController SessionEditController,
        AgentOpenSessionController OpenSessionController,
        AgentPromptContextService PromptContextService,
        AgentOutputInsertController OutputInsertController,
        AgentConfirmationController ConfirmationController,
        AgentTabToolController TabToolController,
        AgentSelectionContextController SelectionContextController,
        AgentFileToolController FileToolController,
        AgentToolExecutionController ToolExecutionController,
        AgentContextStatsController ContextStatsController,
        AgentPlanController PlanController,
        AgentSessionRewindController SessionRewindController,
        AgentRunOutputController RunOutputController,
        AgentRunWorkspaceResolver RunWorkspaceResolver,
        AgentRunTextFormatter RunTextFormatter,
        AgentModelContextLimitProvider ModelContextLimits,
        AgentLlmToolCatalog LlmToolCatalog,
        AgentResponseInspector ResponseInspector,
        AgentResponseStreamService ResponseStreamService,
        AgentSessionHistoryCoordinator SessionHistoryCoordinator)
    {
        public static AgentControllerComposition Create(
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
            Dictionary<string, AgentRunContext> runningSessions,
            SemaphoreSlim toolExecutionSessionGate,
            Func<string> currentSessionIdProvider,
            Action<string> setCurrentSessionId,
            Func<string> sessionHistoryTextProvider,
            Func<double> sessionHistoryTokenCountProvider,
            Func<string, Task>? fileModifiedAsync,
            Func<string, Task<AgentOpenFileResult>>? openFileInEditorAsync,
            Action? beforeDialog,
            Action? afterDialog,
            Func<string, string, bool, Task>? revertTabOrFileAsync,
            Action<string>? closeTabById,
            Func<string, Task>? navigateToFolderAsync,
            Func<OpenedTab, string?, Task<bool>>? saveTabAsync,
            Func<OpenedTab, string, Task<bool>>? editTabAsync,
            Func<string?, Task<bool>>? beginStreamIntoActiveEditorAsync,
            Func<string?, string, Task<bool>>? streamTextIntoActiveEditorAsync,
            Func<string?, Task>? endStreamIntoActiveEditorAsync,
            AgentControllerCompositionCallbacks callbacks)
        {
            var uiDispatcher = new AgentUiDispatcher(agentPane.DispatcherQueue);
            var displayText = new AgentDisplayLocalizer(getString);
            var runTextFormatter = new AgentRunTextFormatter(getString);
            var runWorkspaceResolver = new AgentRunWorkspaceResolver(() => fileTools.WorkspaceRoot);
            var modelContextLimits = new AgentModelContextLimitProvider();
            var llmToolCatalog = new AgentLlmToolCatalog();
            var responseInspector = new AgentResponseInspector();

            fileTools.WorkspaceRootOverrideProvider = callbacks.GetActiveRunWorkspaceRoot;

            var attachmentController = new AgentAttachmentController(
                agentPane,
                initializePickerWindow,
                showError,
                getString,
                displayText,
                callbacks.IsCurrentSessionRunning,
                callbacks.UpdateContextStats,
                AgentTokenEstimator.Estimate,
                pdfTextExtractionService,
                beforeDialog,
                afterDialog);
            var selectionContextController = new AgentSelectionContextController(
                activeTabProvider,
                () => fileTools.WorkspaceRoot);
            var fileToolController = new AgentFileToolController(
                fileTools,
                selectionContextController,
                callbacks.GetActiveTabForContext,
                callbacks.IsCurrentSessionRunning,
                getString,
                openFileInEditorAsync);
            var presetController = new AgentPresetController(
                agentPane,
                initializePickerWindow,
                showError,
                getString,
                callbacks.UpdateContextStatsSlow,
                beforeDialog,
                afterDialog);
            var skillController = new AgentSkillController(
                agentPane,
                getString,
                callbacks.UpdateContextStatsSlow);
            var mcpController = new AgentMcpController(
                agentPane,
                initializePickerWindow,
                settingsService,
                credentialService,
                showError,
                getString,
                callbacks.UpdateContextStatsSlow,
                () => fileTools.WorkspaceRoot,
                fileModifiedAsync,
                beforeDialog,
                afterDialog,
                callbacks.AddCurrentRunImageToolAttachment);
            var historyController = new AgentHistoryController(agentPane);
            var sessionEditController = new AgentSessionEditController(
                agentPane,
                async action => await uiDispatcher.RunAsync<bool>(async () =>
                {
                    await action();
                    return true;
                }),
                fileModifiedAsync,
                revertTabOrFileAsync,
                closeTabById,
                callbacks.AppendActivity,
                showError,
                getString,
                currentSessionIdProvider);
            var openSessionController = new AgentOpenSessionController(
                settingsService,
                agentPane,
                fileTools,
                attachmentController,
                sessionEditController,
                historyController,
                displayText,
                runningSessions,
                currentSessionIdProvider,
                setCurrentSessionId,
                sessionHistoryTextProvider,
                sessionHistoryTokenCountProvider,
                callbacks.GetCurrentRunTranscriptTokens,
                callbacks.RestoreSessionHistoryState,
                callbacks.StopAgent,
                () => callbacks.UpdateContextStatsImmediate(false),
                AppBadgeNotificationService.UpdateBadge,
                getString,
                navigateToFolderAsync);
            var sessionHistoryCoordinator = new AgentSessionHistoryCoordinator(
                agentPane,
                settingsService,
                historyController,
                openSessionController,
                callbacks.IsCurrentSessionRunning,
                currentSessionIdProvider,
                getString);
            var runOutputController = new AgentRunOutputController(
                openSessionController,
                insertIntoActiveEditorAsync,
                beginStreamIntoActiveEditorAsync,
                streamTextIntoActiveEditorAsync,
                endStreamIntoActiveEditorAsync);
            var responseStreamService = new AgentResponseStreamService(
                llmService,
                agentPane,
                uiDispatcher,
                runOutputController,
                openSessionController,
                displayText,
                responseInspector,
                callbacks.UpdateContextStatsImmediate,
                getString);
            var sessionRewindController = new AgentSessionRewindController(
                callbacks.IsCurrentSessionRunning,
                toolExecutionSessionGate,
                currentSessionIdProvider,
                openSessionController.SaveActiveFromUI,
                openSessionController.EnsureSession,
                openSessionController.RestoreSession,
                openSessionController.ClearThinkingState,
                sessionEditController,
                historyController,
                displayText,
                showError,
                getString);
            var workspaceContextBuilder = new AgentWorkspaceContextBuilder(
                () => fileTools.WorkspaceRoot,
                openTabsProvider,
                attachmentController);
            var promptContextService = new AgentPromptContextService(
                agentPane,
                fileTools,
                presetController,
                skillController,
                mcpController,
                workspaceContextBuilder,
                callbacks.GetActiveTabForContext,
                callbacks.CaptureActiveSelectionSnapshot,
                callbacks.GetCurrentSessionSettings,
                sessionHistoryTextProvider,
                getString);
            var contextStatsController = new AgentContextStatsController(
                settingsService,
                agentPane,
                displayText,
                attachmentController,
                callbacks.IsCurrentSessionRunning,
                callbacks.GetActiveTabForContext,
                callbacks.GetActiveSelectionText,
                callbacks.BuildActiveSelectionContext,
                promptContextService.BuildFixedPromptContext,
                promptContextService.BuildConversationTurn,
                promptContextService.BuildWorkspaceContext,
                promptContextService.BuildSessionHistoryForPrompt,
                callbacks.GetCurrentRunTranscriptTokens,
                sessionHistoryCoordinator.RefreshOutputDisplay,
                getString,
                modelContextLimits,
                promptContextService.EstimateToolCatalogTokens,
                skillController.HasSelectedSkills,
                mcpController.HasSelectedMcpServers,
                callbacks.GetCurrentSessionSettings);
            var outputInsertController = new AgentOutputInsertController(
                agentPane,
                openNewTabWithContent,
                openSessionController.GetCurrentLastAnswerText,
                showError,
                getString,
                displayText.IsOutputPlaceholder,
                () => uiDispatcher.RunAsync(() => { }));
            var tabToolController = new AgentTabToolController(
                callbacks.GetActiveTabForContext,
                activeTabProvider,
                openTabsProvider,
                getTabText,
                insertIntoActiveEditorAsync,
                openNewTabWithContent,
                saveTabAsync,
                editTabAsync,
                fileTools,
                sessionEditController,
                action => uiDispatcher.RunAsync(action),
                action => uiDispatcher.RunAsync(action),
                action => uiDispatcher.RunAsync(action));
            var confirmationController = new AgentConfirmationController(
                settingsService,
                agentPane,
                fileTools,
                isGitRepoProvider,
                async action => await uiDispatcher.RunAsync(action),
                callbacks.AppendActivity,
                getString);
            var planController = new AgentPlanController(
                callbacks.GetActiveRunContext,
                confirmationController,
                openTabsProvider,
                openFileInEditorAsync,
                saveTabAsync,
                action => uiDispatcher.RunAsync(action),
                async action => await uiDispatcher.RunAsync(action),
                async action => await uiDispatcher.RunAsync(action),
                runOutputController.AppendRunActivityAsync,
                runOutputController.AppendRunOutputLineAsync,
                showError,
                getString);
            var toolExecutionController = new AgentToolExecutionController(
                llmService,
                fileTools,
                fileToolController,
                tabToolController,
                skillController,
                mcpController,
                callbacks.AddCurrentRunImageToolAttachment,
                planController.MakePlanAsync,
                callbacks.AppendActivity,
                getString);

            fileTools.ConfirmFileEditAsync = confirmationController.ConfirmFileEditAsync;
            fileTools.ConfirmPowerShellAsync = confirmationController.ConfirmPowerShellAsync;
            fileTools.FileEditCommittedAsync = preview => uiDispatcher.RunAsync(() => sessionEditController.Track(preview));
            fileTools.ActivityReporter = callbacks.AppendActivity;
            if (fileModifiedAsync != null)
            {
                fileTools.FileModifiedAsync = fileModifiedAsync;
            }

            return new AgentControllerComposition(
                uiDispatcher,
                displayText,
                attachmentController,
                presetController,
                skillController,
                mcpController,
                historyController,
                sessionEditController,
                openSessionController,
                promptContextService,
                outputInsertController,
                confirmationController,
                tabToolController,
                selectionContextController,
                fileToolController,
                toolExecutionController,
                contextStatsController,
                planController,
                sessionRewindController,
                runOutputController,
                runWorkspaceResolver,
                runTextFormatter,
                modelContextLimits,
                llmToolCatalog,
                responseInspector,
                responseStreamService,
                sessionHistoryCoordinator);
        }
    }
}
