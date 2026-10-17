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
        private const string PlanFolderName = "tmp";
        private const string FallbackPlanFolderName = "plan";

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
            builder.AppendLine("Do not use ordinary file creation or tab saving tools for the plan. The make_plan tool chooses the filename, saves the plan in the 'tmp' folder at the workspace root, and opens the saved plan.");
            builder.AppendLine("The make_plan Markdown must include target files, edit scope, areas not to touch, evidence/context summary, step-by-step implementation, verification, and rollback/failure criteria.");
            builder.AppendLine();
            builder.AppendLine("[Original user request]");
            builder.Append(userInstruction);
            return builder.ToString().Trim();
        }

        public static string BuildPlanRevisionRequest(string userInstruction, string planPath, string targetLanguage = "Korean")
        {
            string examplePlanPath = planPath.Replace(Path.DirectorySeparatorChar, '/');
            var builder = new StringBuilder();
            builder.AppendLine("[Planning-mode task] (plan revision)");
            builder.AppendLine("The previously saved plan was not approved. Revise that plan file instead of creating a new plan.");
            builder.AppendLine($"- Plan file to revise: {planPath}");
            builder.AppendLine("- In this revision run, file edit tools (apply_patch, search_replace, replace_range, overwrite_file, append_to_file, insert_to_file) are allowed only for that plan file; all other files are read-only.");
            builder.AppendLine("- Read the current plan file first, then apply the requested changes. Keep the document structure and header sections intact.");
            builder.AppendLine("- When the revision is complete, request approval again with exactly one make_plan tool_call that passes the plan file path:");
            builder.AppendLine($"<tool_call>{{\"name\":\"make_plan\",\"arguments\":{{\"path\":\"{examplePlanPath}\"}}}}</tool_call>");
            builder.AppendLine("- Do not create a new plan file. Pass the markdown argument only when you are providing the complete revised plan content.");
            builder.AppendLine("- If the user's request is unrelated to this plan or asks for a new plan, ignore the above and run a normal planning run: investigate, then call make_plan with the markdown argument and no path.");
            builder.AppendLine($"The plan MUST be written in {targetLanguage}.");
            builder.AppendLine();
            builder.AppendLine("[User revision request]");
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
            string requestedPlanPath = GetFirstStringArgument(arguments, "path", "planPath", "plan_path", "file", "filePath", "file_path");
            if (string.IsNullOrWhiteSpace(planMarkdown) && string.IsNullOrWhiteSpace(requestedPlanPath))
            {
                return "make_plan failed: provide markdown for a new plan, or the path of an existing plan file to re-submit it for approval.";
            }

            string workspaceRoot = runContext.WorkspaceRoot;
            try
            {
                string planPath;
                bool resubmitted = !string.IsNullOrWhiteSpace(requestedPlanPath);
                if (resubmitted)
                {
                    string validationError = ValidateResubmittedPlanPath(runContext, requestedPlanPath, workspaceRoot, out planPath);
                    if (!string.IsNullOrEmpty(validationError))
                    {
                        return validationError;
                    }

                    if (!string.IsNullOrWhiteSpace(planMarkdown))
                    {
                        await UpdatePlanFileBodyAsync(planPath, planMarkdown, cancellationToken);
                    }
                }
                else
                {
                    planPath = await SavePlanFileAsync(
                        runContext.OriginalUserInstruction,
                        planMarkdown,
                        workspaceRoot,
                        runContext.PlanWorkspaceContext,
                        runContext.PlanSelectionContext,
                        cancellationToken);
                }

                runContext.GeneratedPlanPath = planPath;
                runContext.PendingPlanPath = planPath;
                runContext.PlanResubmittedForApproval = resubmitted;
                await OpenPlanFileForReviewAsync(runContext, planPath);
                return resubmitted
                    ? $"make_plan resubmitted: {planPath}\nworkspace_root: {workspaceRoot}"
                    : $"make_plan saved: {planPath}\nworkspace_root: {workspaceRoot}";
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
            string planOutputFormatKey = runContext.PlanResubmittedForApproval
                ? "AgentPlanResubmittedOutputFormat"
                : "AgentPlanSavedOutputFormat";
            string planOutputFormat = runContext.PlanResubmittedForApproval
                ? "수정된 계획서로 다시 승인을 요청합니다: {0}"
                : "계획서를 저장하고 열었습니다: {0}";
            await _appendRunOutputLineAsync(runContext, string.Format(
                _getString(planOutputFormatKey, planOutputFormat),
                planPath));

            bool approved = await _confirmationController.ConfirmPlanExecutionAsync(planPath, cancellationToken);
            if (!approved)
            {
                await _appendRunOutputLineAsync(
                    runContext,
                    _getString("AgentPlanExecutionCancelledOutput", "계획 실행이 취소되었습니다."));
                await _appendRunOutputLineAsync(
                    runContext,
                    _getString(
                        "AgentPlanRevisionHint",
                        "계획을 수정하려면 수정 요청을 입력하세요. Agent가 계획서를 수정한 뒤 승인을 다시 요청합니다."));
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
            string planDirectory = ResolvePlanDirectory(workspaceRoot);
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

        private static string ValidateResubmittedPlanPath(
            AgentRunContext runContext,
            string requestedPlanPath,
            string workspaceRoot,
            out string fullPlanPath)
        {
            fullPlanPath = ResolveFullPath(requestedPlanPath, workspaceRoot);
            if (string.IsNullOrWhiteSpace(fullPlanPath) || !File.Exists(fullPlanPath))
            {
                return $"make_plan failed: plan file not found: {requestedPlanPath}";
            }

            if (!string.IsNullOrWhiteSpace(runContext.PendingPlanPath))
            {
                return AreSameFile(fullPlanPath, runContext.PendingPlanPath, workspaceRoot)
                    ? string.Empty
                    : $"make_plan failed: path must be the plan file awaiting approval: {runContext.PendingPlanPath}";
            }

            string planDirectory = ResolveFullPath(ResolvePlanDirectory(workspaceRoot), workspaceRoot);
            string directoryPrefix = planDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.IsNullOrWhiteSpace(planDirectory) ||
                !fullPlanPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "make_plan failed: path must point to a plan file saved under the workspace plan folder.";
            }

            return string.Empty;
        }

        private static async Task UpdatePlanFileBodyAsync(string planPath, string planMarkdown, CancellationToken cancellationToken)
        {
            string existingContent = await File.ReadAllTextAsync(planPath, cancellationToken);
            int planHeadingIndex = FindPlanHeadingIndex(existingContent);
            string updatedContent;
            if (planHeadingIndex >= 0)
            {
                int headingLineEnd = existingContent.IndexOf('\n', planHeadingIndex);
                string prefix = headingLineEnd >= 0
                    ? existingContent.Substring(0, headingLineEnd + 1)
                    : existingContent + Environment.NewLine;
                updatedContent = prefix + Environment.NewLine + planMarkdown.Trim() + Environment.NewLine;
            }
            else
            {
                updatedContent = planMarkdown.TrimEnd() + Environment.NewLine;
            }

            await File.WriteAllTextAsync(planPath, updatedContent, Encoding.UTF8, cancellationToken);
        }

        private static int FindPlanHeadingIndex(string content)
        {
            int lineStart = 0;
            while (lineStart < content.Length)
            {
                int lineEnd = content.IndexOf('\n', lineStart);
                int lineLength = (lineEnd < 0 ? content.Length : lineEnd) - lineStart;
                string line = content.Substring(lineStart, lineLength).Trim();
                if (line.Equals("## Plan", StringComparison.OrdinalIgnoreCase))
                {
                    return lineStart;
                }

                if (lineEnd < 0)
                {
                    break;
                }

                lineStart = lineEnd + 1;
            }

            return -1;
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

        private static string ResolvePlanDirectory(string workspaceRoot)
        {
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                try
                {
                    string root = Path.GetFullPath(workspaceRoot);
                    if (Directory.Exists(root))
                    {
                        return Path.Combine(root, PlanFolderName);
                    }
                }
                catch
                {
                }
            }

            return Path.Combine(AgentSkillDirectories.UserSettingsDirectory, FallbackPlanFolderName);
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
