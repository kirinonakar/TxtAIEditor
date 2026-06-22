using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentConfirmationController
    {
        private readonly ISettingsService _settingsService;
        private readonly AgentPane _agentPane;
        private readonly AgentFileToolService _fileTools;
        private readonly Func<string, bool> _isGitRepoProvider;
        private readonly Func<Func<Task<bool>>, Task<bool>> _runOnUIThreadAsync;
        private readonly Action<string> _appendActivity;
        private readonly Func<string, string, string> _getString;
        private TaskCompletionSource<bool>? _diffApprovalTcs;

        public AgentConfirmationController(
            ISettingsService settingsService,
            AgentPane agentPane,
            AgentFileToolService fileTools,
            Func<string, bool> isGitRepoProvider,
            Func<Func<Task<bool>>, Task<bool>> runOnUIThreadAsync,
            Action<string> appendActivity,
            Func<string, string, string> getString)
        {
            _settingsService = settingsService;
            _agentPane = agentPane;
            _fileTools = fileTools;
            _isGitRepoProvider = isGitRepoProvider;
            _runOnUIThreadAsync = runOnUIThreadAsync;
            _appendActivity = appendActivity;
            _getString = getString;
        }

        public void ApprovePending()
        {
            _diffApprovalTcs?.TrySetResult(true);
        }

        public void CancelPending()
        {
            _diffApprovalTcs?.TrySetResult(false);
        }

        public async Task<bool> ConfirmFileEditAsync(AgentFileEditPreview preview)
        {
            var settings = _settingsService.CurrentSettings;
            string root = _fileTools.WorkspaceRoot;
            if (settings.LlmAgentAutoApproveGitEdits && _isGitRepoProvider(root))
            {
                _appendActivity(string.Format(
                    _getString("AgentActivityDiffAppliedFormat", "변경 적용 승인: {0}"),
                    preview.RelativePath));
                return true;
            }

            _appendActivity(string.Format(
                _getString("AgentActivityDiffReviewFormat", "파일 변경 승인 대기 중: {0}"),
                preview.RelativePath));

            return await _runOnUIThreadAsync(async () =>
            {
                string titleKey = preview.ActionName switch
                {
                    "create_file" => "AgentCreateDialogTitle",
                    _ => "AgentEditDialogTitle"
                };

                string defaultTitle = preview.ActionName switch
                {
                    "create_file" => "Agent 파일 생성 확인: {0}",
                    _ => "Agent 파일 수정 확인: {0}"
                };

                string summaryText = preview.IsNewFile
                    ? string.Format(_getString("AgentCreateSummaryFormat", "파일을 생성하시겠습니까? 경로: {0}"), preview.RelativePath)
                    : string.Format(_getString("AgentEditSummaryFormat", "파일을 수정하시겠습니까? 경로: {0}"), preview.RelativePath);

                string headerText = string.Format(
                    _getString(titleKey, defaultTitle),
                    Path.GetFileName(preview.RelativePath));

                _agentPane.ShowDiffConfirm(headerText, summaryText);

                _diffApprovalTcs = new TaskCompletionSource<bool>();
                bool approved = await _diffApprovalTcs.Task;
                _agentPane.HideDiffConfirm();

                _appendActivity(approved
                    ? string.Format(_getString("AgentActivityDiffAppliedFormat", "변경 적용 승인: {0}"), preview.RelativePath)
                    : string.Format(_getString("AgentActivityDiffCancelledFormat", "변경 적용 취소: {0}"), preview.RelativePath));

                return approved;
            });
        }

        public async Task<bool> ConfirmPowerShellAsync(string command)
        {
            _appendActivity(_getString("AgentActivityPowerShellConfirmationPending", "PowerShell 실행 승인 대기 중"));

            return await _runOnUIThreadAsync(async () =>
            {
                string headerText = _getString("AgentPowerShellConfirmHeader", "PowerShell 실행 확인");
                string wrappedCommand = AgentToolHelpers.WrapForConfirmation(command);
                string summaryText = string.Format(
                    _getString("AgentPowerShellConfirmSummaryFormat", "아래 명령을 실행하시겠습니까?\n\n{0}"),
                    string.Empty).TrimEnd();

                _agentPane.ShowPowerShellConfirm(headerText, summaryText, wrappedCommand);

                _diffApprovalTcs = new TaskCompletionSource<bool>();
                bool approved = await _diffApprovalTcs.Task;
                _agentPane.HideDiffConfirm();

                _appendActivity(approved
                    ? _getString("AgentActivityPowerShellApproved", "PowerShell 실행 승인됨")
                    : _getString("AgentActivityPowerShellCancelled", "PowerShell 실행 취소됨"));

                return approved;
            });
        }

        public async Task<bool> ConfirmPlanExecutionAsync(string planPath, CancellationToken cancellationToken = default)
        {
            _appendActivity(_getString("AgentActivityPlanApprovalPending", "계획 실행 승인 대기 중"));

            return await _runOnUIThreadAsync(async () =>
            {
                string headerText = _getString("AgentPlanApprovalHeader", "계획 실행 승인");
                string summaryText = string.Format(
                    _getString(
                        "AgentPlanApprovalDescriptionFormat",
                        "열린 계획서를 검토, 수정, 저장한 뒤 승인하면 새 세션에서 계획을 실행합니다.\n\n계획서: {0}"),
                    planPath);

                _agentPane.ShowDiffConfirm(headerText, summaryText);

                var approvalTcs = new TaskCompletionSource<bool>();
                _diffApprovalTcs = approvalTcs;
                using CancellationTokenRegistration registration =
                    cancellationToken.Register(() => approvalTcs.TrySetCanceled(cancellationToken));

                bool approved;
                try
                {
                    approved = await approvalTcs.Task;
                }
                finally
                {
                    _agentPane.HideDiffConfirm();
                }

                _appendActivity(approved
                    ? _getString("AgentActivityPlanApproved", "계획 실행 승인됨")
                    : _getString("AgentActivityPlanCancelled", "계획 실행 취소됨"));

                return approved;
            });
        }
    }
}
