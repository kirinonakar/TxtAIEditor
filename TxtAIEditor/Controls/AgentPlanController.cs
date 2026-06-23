using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentPlanController
    {
        private readonly Func<AgentRunContext?> _activeRunContextProvider;
        private readonly AgentConfirmationController _confirmationController;
        private readonly Func<IReadOnlyList<OpenedTab>> _openTabsProvider;
        private readonly Func<string, Task<AgentOpenFileResult>>? _openFileInEditorAsync;
        private readonly Func<OpenedTab, string?, Task<bool>>? _saveTabAsync;
        private readonly Func<Action, Task> _runActionOnUIThreadAsync;
        private readonly Func<Func<Task<bool>>, Task<bool>> _runBoolOnUIThreadAsync;
        private readonly Func<Func<Task<AgentOpenFileResult>>, Task<AgentOpenFileResult>> _runOpenFileResultOnUIThreadAsync;
        private readonly Func<AgentRunContext, string, Task> _appendRunActivityAsync;
        private readonly Func<AgentRunContext, string, Task> _appendRunOutputLineAsync;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;

        public AgentPlanController(
            Func<AgentRunContext?> activeRunContextProvider,
            AgentConfirmationController confirmationController,
            Func<IReadOnlyList<OpenedTab>> openTabsProvider,
            Func<string, Task<AgentOpenFileResult>>? openFileInEditorAsync,
            Func<OpenedTab, string?, Task<bool>>? saveTabAsync,
            Func<Action, Task> runActionOnUIThreadAsync,
            Func<Func<Task<bool>>, Task<bool>> runBoolOnUIThreadAsync,
            Func<Func<Task<AgentOpenFileResult>>, Task<AgentOpenFileResult>> runOpenFileResultOnUIThreadAsync,
            Func<AgentRunContext, string, Task> appendRunActivityAsync,
            Func<AgentRunContext, string, Task> appendRunOutputLineAsync,
            Action<string, string> showError,
            Func<string, string, string> getString)
        {
            _activeRunContextProvider = activeRunContextProvider;
            _confirmationController = confirmationController;
            _openTabsProvider = openTabsProvider;
            _openFileInEditorAsync = openFileInEditorAsync;
            _saveTabAsync = saveTabAsync;
            _runActionOnUIThreadAsync = runActionOnUIThreadAsync;
            _runBoolOnUIThreadAsync = runBoolOnUIThreadAsync;
            _runOpenFileResultOnUIThreadAsync = runOpenFileResultOnUIThreadAsync;
            _appendRunActivityAsync = appendRunActivityAsync;
            _appendRunOutputLineAsync = appendRunOutputLineAsync;
            _showError = showError;
            _getString = getString;
        }

        public static string BuildPlanningModeRequest(string userInstruction, string targetLanguage = "Korean")
        {
            var builder = new StringBuilder();
            builder.AppendLine("[Planning-mode task]");
            builder.AppendLine("Investigate first, then call make_plan with the detailed Markdown plan.");
            builder.AppendLine($"The plan (Markdown content for the make_plan tool) MUST be written in {targetLanguage}.");
            builder.AppendLine("Do not edit, create, delete, move, format, build, restore, install, stage, commit, or save files in this run, except by calling make_plan once for the plan document.");
            builder.AppendLine("Do not use ordinary file creation or tab saving tools for the plan. The make_plan tool chooses the filename and opens the saved plan.");
            builder.AppendLine("The make_plan Markdown must include target files, edit scope, areas not to touch, evidence/context summary, step-by-step implementation, verification, and rollback/failure criteria.");
            builder.AppendLine();
            builder.AppendLine("[Original user request]");
            builder.Append(userInstruction);
            return builder.ToString().Trim();
        }

        public async Task<string> MakePlanAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            AgentRunContext? runContext = _activeRunContextProvider();
            if (runContext == null)
            {
                return "make_plan failed: no active planning run context.";
            }

            if (!runContext.IsPlanningMode)
            {
                return "make_plan failed: this tool is only available when planning mode is enabled.";
            }

            string planMarkdown = GetFirstStringArgument(arguments, "markdown", "content", "plan", "md", "text");
            if (string.IsNullOrWhiteSpace(planMarkdown))
            {
                return "make_plan failed: markdown content is empty.";
            }

            string workspaceRoot = runContext.WorkspaceRoot;
            try
            {
                string planPath = await SavePlanFileAsync(
                    runContext.OriginalUserInstruction,
                    planMarkdown,
                    workspaceRoot,
                    runContext.PlanWorkspaceContext,
                    runContext.PlanSelectionContext,
                    cancellationToken);
                runContext.GeneratedPlanPath = planPath;
                await OpenPlanFileForReviewAsync(runContext, planPath);
                return $"make_plan saved: {planPath}\nworkspace_root: {workspaceRoot}";
            }
            catch (Exception ex)
            {
                string title = _getString("AgentPlanSaveErrorTitle", "계획 저장 오류");
                string message = string.Format(
                    _getString("AgentPlanSaveErrorMessage", "계획서를 저장하는 중 오류가 발생했습니다: {0}"),
                    ex.Message);
                await _runActionOnUIThreadAsync(() => _showError(title, message));
                await _appendRunActivityAsync(runContext, message);
                return $"make_plan failed: {ex.Message}";
            }
        }

        public async Task<string> WaitForSavedPlanApprovalAsync(
            AgentRunContext runContext,
            string originalUserInstruction,
            CancellationToken cancellationToken)
        {
            string planPath = runContext.GeneratedPlanPath;
            if (string.IsNullOrWhiteSpace(planPath))
            {
                string message = _getString(
                    "AgentPlanPathMissing",
                    "저장된 계획서 경로를 찾지 못해 실행을 시작하지 않았습니다.");
                await _appendRunActivityAsync(runContext, message);
                await _appendRunOutputLineAsync(runContext, message);
                return string.Empty;
            }

            await _appendRunActivityAsync(runContext, string.Format(
                _getString("AgentActivityPlanSavedFormat", "계획서 저장됨: {0}"),
                planPath));
            await _appendRunOutputLineAsync(runContext, string.Format(
                _getString("AgentPlanSavedOutputFormat", "계획서를 저장하고 열었습니다: {0}"),
                planPath));

            bool approved = await _confirmationController.ConfirmPlanExecutionAsync(planPath, cancellationToken);
            if (!approved)
            {
                await _appendRunOutputLineAsync(
                    runContext,
                    _getString("AgentPlanExecutionCancelledOutput", "계획 실행이 취소되었습니다."));
                return string.Empty;
            }

            bool saved = await SaveOpenPlanTabIfDirtyAsync(planPath);
            if (!saved)
            {
                string message = _getString(
                    "AgentPlanSaveDirtyTabFailed",
                    "수정된 계획 탭을 저장하지 못해 실행을 시작하지 않았습니다.");
                await _appendRunActivityAsync(runContext, message);
                await _appendRunOutputLineAsync(runContext, message);
                return string.Empty;
            }

            string approvedPlan = await File.ReadAllTextAsync(planPath, cancellationToken);
            string workspaceRoot = runContext.WorkspaceRoot;
            return BuildApprovedPlanExecutionPrompt(
                originalUserInstruction,
                approvedPlan,
                planPath,
                workspaceRoot);
        }

        private async Task<string> SavePlanFileAsync(
            string originalUserInstruction,
            string planMarkdown,
            string workspaceRoot,
            string workspaceContext,
            string selectionContext,
            CancellationToken cancellationToken)
        {
            string planDirectory = Path.Combine(AgentSkillDirectories.UserSettingsDirectory, "plan");
            Directory.CreateDirectory(planDirectory);

            DateTime createdAt = DateTime.Now;
            string planPath = GetNextPlanFilePath(planDirectory, createdAt);
            string content = BuildPlanFileContent(
                originalUserInstruction,
                planMarkdown,
                workspaceRoot,
                workspaceContext,
                selectionContext,
                createdAt);

            await File.WriteAllTextAsync(planPath, content, Encoding.UTF8, cancellationToken);
            return planPath;
        }

        private async Task OpenPlanFileForReviewAsync(AgentRunContext runContext, string planPath)
        {
            if (_openFileInEditorAsync == null)
            {
                return;
            }

            AgentOpenFileResult openResult = await _runOpenFileResultOnUIThreadAsync(() => _openFileInEditorAsync(planPath));
            if (openResult.Success)
            {
                return;
            }

            string message = string.Format(
                _getString("AgentPlanOpenFailedFormat", "계획서를 열지 못했습니다: {0}"),
                string.IsNullOrWhiteSpace(openResult.ErrorMessage) ? planPath : openResult.ErrorMessage);
            await _appendRunActivityAsync(runContext, message);
            await _appendRunOutputLineAsync(runContext, message);
        }

        private async Task<bool> SaveOpenPlanTabIfDirtyAsync(string planPath)
        {
            if (_saveTabAsync == null)
            {
                return true;
            }

            return await _runBoolOnUIThreadAsync(async () =>
            {
                string normalizedPlanPath = NormalizeFullPath(planPath);
                OpenedTab? planTab = _openTabsProvider()
                    .FirstOrDefault(tab =>
                        !string.IsNullOrWhiteSpace(tab.FilePath) &&
                        string.Equals(NormalizeFullPath(tab.FilePath), normalizedPlanPath, StringComparison.OrdinalIgnoreCase));

                if (planTab == null || !planTab.IsDirty)
                {
                    return true;
                }

                if (planTab.IsReadOnlyViewer)
                {
                    return false;
                }

                return await _saveTabAsync(planTab, null);
            });
        }

        private static string BuildApprovedPlanExecutionPrompt(
            string originalUserInstruction,
            string approvedPlan,
            string planPath,
            string workspaceRoot)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[Approved plan execution]");
            builder.AppendLine("Execute the approved plan below in this fresh session.");
            builder.AppendLine("The previous planning transcript is intentionally omitted; use only this compact context and the approved plan as starting context.");
            builder.AppendLine("Re-read the relevant files before editing, keep the scope limited to the plan, and do not perform unrelated refactors, formatting, renames, moves, commits, or dependency changes.");
            builder.AppendLine($"Use this workspace root for all relative paths: {workspaceRoot}");
            builder.AppendLine($"The plan file may be open in the editor, but its folder is not the working directory: {planPath}");
            builder.AppendLine("Do not edit the plan file unless the approved plan explicitly asks for it.");
            builder.AppendLine();
            builder.AppendLine("[Original user request]");
            builder.AppendLine(originalUserInstruction);
            builder.AppendLine();
            builder.AppendLine("[Approved plan]");
            builder.AppendLine(approvedPlan);
            return builder.ToString().Trim();
        }

        private static string BuildPlanFileContent(
            string originalUserInstruction,
            string planMarkdown,
            string workspaceRoot,
            string workspaceContext,
            string selectionContext,
            DateTime createdAt)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# TxtAIEditor Agent Plan");
            builder.AppendLine();
            builder.AppendLine($"- Created: {createdAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"- Workspace root: {workspaceRoot}");
            builder.AppendLine();
            builder.AppendLine("## Original Request");
            builder.AppendLine();
            AppendFencedBlock(builder, originalUserInstruction);
            builder.AppendLine();
            builder.AppendLine("## Compact Context");
            builder.AppendLine();
            AppendFencedBlock(builder, workspaceContext);
            if (!string.IsNullOrWhiteSpace(selectionContext))
            {
                builder.AppendLine();
                builder.AppendLine("## Selection Context");
                builder.AppendLine();
                AppendFencedBlock(builder, selectionContext);
            }

            builder.AppendLine();
            builder.AppendLine("## Plan");
            builder.AppendLine();
            builder.AppendLine(planMarkdown.Trim());
            builder.AppendLine();
            return builder.ToString();
        }

        private static void AppendFencedBlock(StringBuilder builder, string value)
        {
            string text = value ?? string.Empty;
            string fence = text.Contains("```", StringComparison.Ordinal) ? "~~~~" : "```";
            builder.AppendLine(fence + "text");
            builder.AppendLine(text);
            builder.AppendLine(fence);
        }

        private static string GetNextPlanFilePath(string planDirectory, DateTime createdAt)
        {
            string dateText = createdAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            for (int i = 1; i <= 9999; i++)
            {
                string candidate = Path.Combine(planDirectory, $"plan_{dateText}_{i:000}.md");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(planDirectory, $"plan_{dateText}_{createdAt:HHmmssfff}.md");
        }

        private static string NormalizeFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
