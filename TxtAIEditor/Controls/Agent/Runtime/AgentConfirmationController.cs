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
        private readonly Func<AgentRunContext?> _activeRunContextProvider;
        private readonly SemaphoreSlim _approvalGate = new(1, 1);
        private TaskCompletionSource<bool>? _diffApprovalTcs;
        private string? _approvalSessionId;

        public AgentConfirmationController(
            ISettingsService settingsService,
            AgentPane agentPane,
            AgentFileToolService fileTools,
            Func<string, bool> isGitRepoProvider,
            Func<Func<Task<bool>>, Task<bool>> runOnUIThreadAsync,
            Action<string> appendActivity,
            Func<string, string, string> getString,
            Func<AgentRunContext?> activeRunContextProvider)
        {
            _settingsService = settingsService;
            _agentPane = agentPane;
            _fileTools = fileTools;
            _isGitRepoProvider = isGitRepoProvider;
            _runOnUIThreadAsync = runOnUIThreadAsync;
            _appendActivity = appendActivity;
            _getString = getString;
            _activeRunContextProvider = activeRunContextProvider;
        }

        public void ApprovePending()
        {
            _diffApprovalTcs?.TrySetResult(true);
        }

        public void CancelPending(string? sessionId = null)
        {
            if (sessionId == null || string.Equals(sessionId, _approvalSessionId, StringComparison.Ordinal))
                _diffApprovalTcs?.TrySetResult(false);
        }

        private async Task<bool> WaitForApprovalAsync(Action show, string? sessionId, CancellationToken cancellationToken)
        {
            await _approvalGate.WaitAsync(cancellationToken);
            var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _approvalSessionId = sessionId;
                _diffApprovalTcs = approval;
                using var registration = cancellationToken.Register(() => approval.TrySetCanceled(cancellationToken));
                show();
                return await approval.Task;
            }
            finally
            {
                _diffApprovalTcs = null;
                _approvalSessionId = null;
                _agentPane.HideDiffConfirm();
                _approvalGate.Release();
            }
        }

        public async Task<bool> ConfirmFileEditAsync(AgentFileEditPreview preview)
        {
            var context = _activeRunContextProvider();
            CancellationToken cancellationToken = context?.Cancellation?.Token ?? CancellationToken.None;
            cancellationToken.ThrowIfCancellationRequested();
            string displayPath = preview.RelativePath;
            await _runOnUIThreadAsync(() =>
            {
                displayPath = _fileTools.GetDisplayPath(preview.FullPath, preview.RelativePath);
                preview.DisplayPath = displayPath;
                return Task.FromResult(true);
            });
            var settings = _settingsService.CurrentSettings;
            string root = _fileTools.WorkspaceRoot;
            if (settings.LlmAgentAutoApproveGitEdits && await Task.Run(() => _isGitRepoProvider(root)))
            {
                _appendActivity(string.Format(
                    _getString("AgentActivityDiffAppliedFormat", "변경 적용 승인: {0}"),
                    displayPath));
                return true;
            }

            _appendActivity(string.Format(
                _getString("AgentActivityDiffReviewFormat", "파일 변경 승인 대기 중: {0}"),
                displayPath));

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
                    ? string.Format(_getString("AgentCreateSummaryFormat", "파일을 생성하시겠습니까? 경로: {0}"), displayPath)
                    : string.Format(_getString("AgentEditSummaryFormat", "파일을 수정하시겠습니까? 경로: {0}"), displayPath);

                string headerText = string.Format(
                    _getString(titleKey, defaultTitle),
                    Path.GetFileName(preview.RelativePath));

                bool approved = await WaitForApprovalAsync(
                    () => _agentPane.ShowDiffConfirm(headerText, summaryText), context?.SessionId, cancellationToken);

                _appendActivity(approved
                    ? string.Format(_getString("AgentActivityDiffAppliedFormat", "변경 적용 승인: {0}"), displayPath)
                    : string.Format(_getString("AgentActivityDiffCancelledFormat", "변경 적용 취소: {0}"), displayPath));

                return approved;
            });
        }

        public async Task<bool> ConfirmPowerShellAsync(string command)
        {
            var context = _activeRunContextProvider();
            CancellationToken cancellationToken = context?.Cancellation?.Token ?? CancellationToken.None;
            cancellationToken.ThrowIfCancellationRequested();
            var settings = _settingsService.CurrentSettings;
            if (settings.LlmAgentAutoApprovePowerShell &&
                !AgentToolHelpers.IsDangerousPowerShellCommand(command))
            {
                _appendActivity(_getString("AgentActivityPowerShellApproved", "PowerShell 실행 승인됨"));
                return true;
            }

            _appendActivity(_getString("AgentActivityPowerShellConfirmationPending", "PowerShell 실행 승인 대기 중"));

            return await _runOnUIThreadAsync(async () =>
            {
                string headerText = _getString("AgentPowerShellConfirmHeader", "PowerShell 실행 확인");
                string wrappedCommand = AgentToolHelpers.WrapForConfirmation(command);
                string summaryText = string.Format(
                    _getString("AgentPowerShellConfirmSummaryFormat", "아래 명령을 실행하시겠습니까?\n\n{0}"),
                    string.Empty).TrimEnd();

                bool approved = await WaitForApprovalAsync(
                    () => _agentPane.ShowPowerShellConfirm(headerText, summaryText, wrappedCommand), context?.SessionId, cancellationToken);

                _appendActivity(approved
                    ? _getString("AgentActivityPowerShellApproved", "PowerShell 실행 승인됨")
                    : _getString("AgentActivityPowerShellCancelled", "PowerShell 실행 취소됨"));

                return approved;
            });
        }

        public async Task<bool> ConfirmPlanExecutionAsync(string planPath, CancellationToken cancellationToken = default)
        {
            string? sessionId = _activeRunContextProvider()?.SessionId;
            var settings = _settingsService.CurrentSettings;
            if (settings.LlmAgentAutoApprovePlanning)
            {
                _appendActivity(_getString("AgentActivityPlanApproved", "계획 실행 승인됨"));
                return true;
            }

            _appendActivity(_getString("AgentActivityPlanApprovalPending", "계획 실행 승인 대기 중"));

            return await _runOnUIThreadAsync(async () =>
            {
                string headerText = _getString("AgentPlanApprovalHeader", "계획 실행 승인");
                string summaryText = string.Format(
                    _getString(
                        "AgentPlanApprovalDescriptionFormat",
                        "열린 계획서를 검토, 수정, 저장한 뒤 승인하면 새 세션에서 계획을 실행합니다.\n\n계획서: {0}"),
                    planPath);

                bool approved = await WaitForApprovalAsync(
                    () => _agentPane.ShowDiffConfirm(headerText, summaryText), sessionId, cancellationToken);

                _appendActivity(approved
                    ? _getString("AgentActivityPlanApproved", "계획 실행 승인됨")
                    : _getString("AgentActivityPlanCancelled", "계획 실행 취소됨"));

                return approved;
            });
        }
    }
}
