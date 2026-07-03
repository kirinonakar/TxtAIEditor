using System;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal static class AgentControllerEventBinder
    {
        public static void Wire(
            AgentPane agentPane,
            Func<Task> runAgentAsync,
            Action stopAgent,
            AgentOpenSessionController openSessionController,
            AgentSessionRewindController sessionRewindController,
            AgentSessionHistoryCoordinator sessionHistoryCoordinator,
            AgentOutputInsertController outputInsertController,
            AgentAttachmentController attachmentController,
            AgentPresetController presetController,
            AgentMcpController mcpController,
            AgentSkillController skillController,
            AgentSessionEditController sessionEditController,
            AgentConfirmationController confirmationController,
            Func<AgentFileEditPreview, Task> openDiffViewAsync,
            Action updateContextStats)
        {
            agentPane.RunRequested += async (_, _) => await runAgentAsync();
            agentPane.StopRequested += (_, _) => stopAgent();
            agentPane.NewSessionRequested += (_, _) =>
            {
                agentPane.PlanningModeCheckBox.IsChecked = false;
                openSessionController.CreateNewSession();
            };
            agentPane.RewindSessionRequested += async (_, _) => await sessionRewindController.RewindCurrentSessionAsync();
            agentPane.OpenSessionsFlyoutOpened += (_, _) =>
            {
                openSessionController.SavePromptTitleFromUI();
                openSessionController.UpdateUI();
            };
            agentPane.OpenSessionSelected += (_, sessionId) => openSessionController.SwitchSession(sessionId);
            agentPane.OpenSessionClosed += (_, sessionId) => openSessionController.CloseSession(sessionId);
            agentPane.HistorySelected += (_, historyId) => sessionHistoryCoordinator.LoadHistorySession(historyId);
            agentPane.HistoryDeleted += async (_, historyId) => await sessionHistoryCoordinator.DeleteHistorySessionAsync(historyId);
            agentPane.HistoryToolbarDeleteClicked += async (_, _) => await sessionHistoryCoordinator.ClearAllHistoryAsync();
            agentPane.InsertOutputRequested += async (_, _) => await outputInsertController.InsertOutputAsync();
            agentPane.InsertNewTabOutputRequested += async (_, _) => await outputInsertController.InsertNewTabOutputAsync();
            agentPane.AddAttachmentRequested += async (_, _) => await attachmentController.AddAttachmentsAsync();
            agentPane.FilesDropped += async (_, filePaths) => await attachmentController.AddDroppedFilesAsync(filePaths);
            agentPane.RemoveAttachmentRequested += (_, attachment) => attachmentController.RemoveAttachment(attachment.Id);
            agentPane.AgentPresetAddRequested += async (_, _) => await presetController.AddPresetAsync();
            agentPane.AgentPresetToggled += (_, presetName) => presetController.TogglePreset(presetName);
            agentPane.AgentPresetEdited += async (_, presetName) => await presetController.EditPresetAsync(presetName);
            agentPane.AgentPresetDeleted += async (_, presetName) => await presetController.DeletePresetAsync(presetName);
            agentPane.AgentPresetRemoved += (_, presetName) => presetController.RemoveSelectedPreset(presetName);
            agentPane.AgentPresetExportRequested += async (_, _) => await presetController.ExportPresetsAsync();
            agentPane.AgentPresetImportRequested += async (_, _) => await presetController.ImportPresetsAsync();
            agentPane.AgentMcpFlyoutOpened += async (_, _) => await mcpController.LoadAsync();
            agentPane.AgentMcpAddRequested += async (_, _) => await mcpController.AddMcpAsync();
            agentPane.AgentMcpExportRequested += async (_, _) => await mcpController.ExportMcpAsync();
            agentPane.AgentMcpImportRequested += async (_, _) => await mcpController.ImportMcpAsync();
            agentPane.AgentMcpToggled += async (_, serverName) => await mcpController.ToggleMcpAsync(serverName);
            agentPane.AgentMcpEdited += async (_, serverName) => await mcpController.EditMcpAsync(serverName);
            agentPane.AgentMcpSettingsRequested += async (_, serverName) => await mcpController.ConfigureBuiltInMcpAsync(serverName);
            agentPane.AgentMcpDeleted += async (_, serverName) => await mcpController.DeleteMcpAsync(serverName);
            agentPane.AgentMcpRemoved += (_, serverName) => mcpController.RemoveSelectedMcp(serverName);
            agentPane.AgentSkillFlyoutOpened += async (_, _) => await skillController.LoadIfNeededAsync();
            agentPane.AgentSkillToggled += (_, skillName) => skillController.ToggleSkill(skillName);
            agentPane.AgentSkillRefreshRequested += async (_, _) => await skillController.LoadAsync();
            agentPane.AgentSkillRemoved += (_, skillName) => skillController.RemoveSelectedSkill(skillName);
            agentPane.Prompt.TextChanged += (_, _) =>
            {
                openSessionController.SavePromptTitleFromUI();
                updateContextStats();
            };
            agentPane.PlanningModeCheckBox.Checked += (_, _) => updateContextStats();
            agentPane.PlanningModeCheckBox.Unchecked += (_, _) => updateContextStats();

            agentPane.DiffApproved += (_, _) => confirmationController.ApprovePending();
            agentPane.DiffCancelled += (_, _) => confirmationController.CancelPending();
            agentPane.FileRevertRequested += async (_, preview) => await sessionEditController.RevertAsync(preview);
            agentPane.FileDiffRequested += async (_, preview) => await openDiffViewAsync(preview);
        }
    }
}
