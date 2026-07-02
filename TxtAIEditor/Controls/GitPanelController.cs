using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class GitPanelController
    {
        private const int GitHistoryBatchSize = 50;

        public event EventHandler<string>? FileRestored;

        private readonly IGitService _gitService;
        private readonly IFileService _fileService;
        private readonly MainWindowViewModel _viewModel;
        private readonly LeftSidebarPane _leftSidebar;
        private readonly TextBlock _statusGitBranch;
        private readonly Func<string> _repoPathProvider;
        private readonly Func<string> _folderPathProvider;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;
        private readonly Action _startAutoRefresh;
        private readonly Func<string, string, string?, string?, string?, string?, string?, Task> _openCompareTabAsync;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly Func<Task>? _refreshExplorerGitStatus;
        private bool _isRefreshingBranchList;
        private bool _isCheckingOutBranch;
        private bool _isLoadingMoreGitHistory;
        private bool _hasMoreGitHistory;
        private int _loadedGitHistoryCount;
        private string _gitHistoryRepoPath = string.Empty;

        public GitPanelController(
            IGitService gitService,
            IFileService fileService,
            MainWindowViewModel viewModel,
            LeftSidebarPane leftSidebar,
            TextBlock statusGitBranch,
            Func<string> repoPathProvider,
            Func<string> folderPathProvider,
            Func<XamlRoot> xamlRootProvider,
            Func<string, string, string> getString,
            Action<string, string> showError,
            Action startAutoRefresh,
            Func<string, string, string?, string?, string?, string?, string?, Task> openCompareTabAsync,
            Action? beforeDialog = null,
            Action? afterDialog = null,
            Func<Task>? refreshExplorerGitStatus = null)
        {
            _gitService = gitService;
            _fileService = fileService;
            _viewModel = viewModel;
            _leftSidebar = leftSidebar;
            _statusGitBranch = statusGitBranch;
            _repoPathProvider = repoPathProvider;
            _folderPathProvider = folderPathProvider;
            _xamlRootProvider = xamlRootProvider;
            _getString = getString;
            _showError = showError;
            _startAutoRefresh = startAutoRefresh;
            _openCompareTabAsync = openCompareTabAsync;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
            _refreshExplorerGitStatus = refreshExplorerGitStatus;

            _leftSidebar.GitChangedFiles.ItemsSource = _viewModel.GitFiles;
            WireEvents();
        }

        public Task RefreshAsync()
        {
            return RefreshAsync(_repoPathProvider());
        }

        private void UpdateInitButtonState(string? repoPath)
        {
            _leftSidebar.GitInitRepoBtn.IsEnabled = string.IsNullOrEmpty(repoPath);
            _leftSidebar.GitPushBtn.IsEnabled = !string.IsNullOrEmpty(repoPath);
            _leftSidebar.GitRemoteBtn.IsEnabled = !string.IsNullOrEmpty(repoPath);
        }

        public async Task RefreshAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
            {
                SetBranchText(GetGitNotDetectedText(), isNotDetected: true);
                _leftSidebar.GitRepoPath.Text = _getString("GitRepoPathNone", "Root 경로없음");
                UpdateInitButtonState(null);
                _viewModel.GitFiles.Clear();
                _isRefreshingBranchList = true;
                try
                {
                    _leftSidebar.GitBranches.Items.Clear();
                }
                finally
                {
                    _isRefreshingBranchList = false;
                }
                _leftSidebar.GitHistory.Items.Clear();
                ResetGitHistoryPaging();
                return;
            }

            string branch = await _gitService.GetCurrentBranchAsync(repoPath);
            bool isGitNotDetected = GitBranchStatus.IsNotDetected(branch);
            string localizedBranch = isGitNotDetected ? GetGitNotDetectedText() : branch;

            // Fetch everything asynchronously first to avoid race conditions and UI flickering
            var branchesTask = _gitService.GetBranchesAsync(repoPath);
            var historyTask = _gitService.GetRecentHistoryAsync(repoPath, GitHistoryBatchSize);
            var fileStatusesTask = _gitService.GetFileStatusesAsync(repoPath);
            var unpushedCountTask = _gitService.GetUnpushedCommitCountAsync(repoPath);

            await Task.WhenAll(branchesTask, historyTask, fileStatusesTask, unpushedCountTask);

            var branches = await branchesTask;
            var historyList = await historyTask;
            var fileStatuses = await fileStatusesTask;
            int unpushedCount = await unpushedCountTask;

            int changedCount = fileStatuses.Count(kvp => kvp.Value != "!!" && kvp.Value.Trim() != "!!");
            string branchText = localizedBranch;
            if (!isGitNotDetected)
            {
                branchText = $"{localizedBranch} ({changedCount})";
                if (unpushedCount > 0)
                {
                    branchText = $"{localizedBranch} ({changedCount}, \u2191 {unpushedCount})";
                }
            }

            // Update UI components in a single synchronous block to prevent duplicate display and empty states
            SetBranchText(branchText, isGitNotDetected);
            _leftSidebar.GitRepoPath.Text = $"{repoPath}";
            ToolTipService.SetToolTip(_leftSidebar.GitRepoPath, repoPath);
            UpdateInitButtonState(isGitNotDetected ? null : repoPath);
            _startAutoRefresh();

            _isRefreshingBranchList = true;
            try
            {
                _leftSidebar.GitBranches.Items.Clear();
                int selectedIndex = -1;
                int i = 0;
                foreach (var branchName in branches)
                {
                    string cleanedBranchName = branchName.Trim();
                    _leftSidebar.GitBranches.Items.Add(cleanedBranchName);
                    if (cleanedBranchName.StartsWith("*", StringComparison.Ordinal))
                    {
                        selectedIndex = i;
                    }
                    i++;
                }
                if (selectedIndex >= 0)
                {
                    _leftSidebar.GitBranches.SelectedIndex = selectedIndex;
                }
            }
            finally
            {
                _isRefreshingBranchList = false;
            }

            _leftSidebar.GitHistory.Items.Clear();
            foreach (var history in historyList)
            {
                _leftSidebar.GitHistory.Items.Add(history);
            }
            _gitHistoryRepoPath = repoPath;
            _loadedGitHistoryCount = historyList.Count;
            _hasMoreGitHistory = historyList.Count >= GitHistoryBatchSize;

            _viewModel.GitFiles.Clear();
            foreach (var kvp in fileStatuses)
            {
                if (kvp.Value == "!!" || kvp.Value.Trim() == "!!")
                {
                    continue; // Do not show ignored files in Git panel changes list
                }
                _viewModel.GitFiles.Add(CreateGitFileItem(kvp.Key, kvp.Value));
            }
            
            if (_refreshExplorerGitStatus != null)
            {
                await _refreshExplorerGitStatus();
            }
        }

        public async Task LoadMoreHistoryAsync(string repoPath)
        {
            if (_isLoadingMoreGitHistory ||
                !_hasMoreGitHistory ||
                string.IsNullOrEmpty(repoPath) ||
                !string.Equals(repoPath, _gitHistoryRepoPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _isLoadingMoreGitHistory = true;
            try
            {
                var moreHistory = await _gitService.GetRecentHistoryAsync(
                    repoPath,
                    GitHistoryBatchSize,
                    _loadedGitHistoryCount);

                if (moreHistory.Count == 0)
                {
                    _hasMoreGitHistory = false;
                    return;
                }

                foreach (var history in moreHistory)
                {
                    _leftSidebar.GitHistory.Items.Add(history);
                }

                _loadedGitHistoryCount += moreHistory.Count;
                _hasMoreGitHistory = moreHistory.Count >= GitHistoryBatchSize;
            }
            finally
            {
                _isLoadingMoreGitHistory = false;
            }
        }

        private string GetGitNotDetectedText()
        {
            return _getString("GitNotDetected", "Git: 감지 안됨");
        }

        private void SetBranchText(string text, bool isNotDetected)
        {
            string? stateTag = isNotDetected ? GitBranchStatus.NotDetectedTag : null;
            _leftSidebar.GitPanelBranch.Text = text;
            _leftSidebar.GitPanelBranch.Tag = stateTag;
            _statusGitBranch.Text = text;
            _statusGitBranch.Tag = stateTag;
        }

        public async Task StageAllAsync(string repoPath)
        {
            bool success = await _gitService.StageAllAsync(repoPath);
            if (success)
            {
                await RefreshAsync(repoPath);
            }
            else
            {
                _showError(
                    _getString("GitStageFailureTitle", "Git Stage 실패"),
                    _getString("GitStageAllFailureMessage", "전체 Stage 처리에 실패했습니다."));
            }
        }

        public async Task ToggleStageAsync(object sender, string repoPath)
        {
            if (sender is not Button { Tag: string filePath })
            {
                return;
            }

            var item = _viewModel.GitFiles.FirstOrDefault(f => f.Path == filePath);
            if (item == null)
            {
                return;
            }

            bool success = item.IsStaged
                ? await _gitService.UnstageFileAsync(repoPath, filePath)
                : await _gitService.StageFileAsync(repoPath, filePath);

            if (success)
            {
                await RefreshAsync(repoPath);
            }
            else
            {
                _showError(
                    _getString("GitStageToggleFailureTitle", "Git Stage 변경 실패"),
                    _getString("GitStageToggleFailureMessage", "Git CLI 명령 처리에 실패했습니다."));
            }
        }

        public async Task OpenChangedFileDiffAsync(string repoPath)
        {
            if (_leftSidebar.GitChangedFiles.SelectedItem is not GitFileItem item)
            {
                return;
            }

            string originalContent = await _gitService.GetGitFileContentAsync(repoPath, item.Path);
            string currentContent = File.Exists(item.Path)
                ? await _fileService.ReadTextFileAsync(item.Path)
                : string.Empty;

            string fileName = GetDisplayName(item.Path);
            await _openCompareTabAsync(
                item.Path,
                item.Path,
                originalContent,
                currentContent,
                string.Format(_getString("GitCompareDiffTitleFormat", "Git 비교: {0}"), fileName),
                string.Format(_getString("GitPreviousVersionFormat", "{0} (이전 버전)"), fileName),
                string.Format(_getString("GitCurrentChangesFormat", "{0} (현재 변경 사항)"), fileName));
        }

        public async Task RestoreFileAsync(object sender, string repoPath)
        {
            if (sender is not Button { Tag: string filePath })
            {
                return;
            }

            bool isDarkTheme = false;
            if (_xamlRootProvider()?.Content is FrameworkElement fe)
            {
                isDarkTheme = fe.ActualTheme == ElementTheme.Dark;
            }

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("GitRestoreFileDialogTitle", "Git 파일 복원"),
                Content = string.Format(_getString("GitRestoreFileDialogContentFormat", "{0} 변경 사항을 복원합니다. Untracked 파일은 삭제됩니다."), GetDisplayName(filePath)),
                PrimaryButtonText = _getString("GitRestoreFileDialogConfirm", "복원"),
                CloseButtonText = _getString("GitRestoreFileDialogCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _afterDialog?.Invoke();
                return;
            }
            _afterDialog?.Invoke();

            bool success = await _gitService.RestoreFileAsync(repoPath, filePath);
            if (success)
            {
                await RefreshAsync(repoPath);
                FileRestored?.Invoke(this, filePath);
            }
            else
            {
                _showError(
                    _getString("GitRestoreFailureTitle", "Git Restore 실패"),
                    _getString("GitRestoreFileFailureMessage", "파일 복원 처리에 실패했습니다."));
            }
        }

        public async Task CommitAsync(string repoPath)
        {
            string message = _leftSidebar.GitCommitMessage.Text;
            if (string.IsNullOrEmpty(message))
            {
                _showError(
                    _getString("GitCommitSuccessTitle", "Git 커밋"),
                    _getString("GitCommitEmptyMessage", "커밋 메시지를 채워주십시오."));
                return;
            }

            bool success = await _gitService.CommitAsync(repoPath, message);
            if (success)
            {
                _leftSidebar.GitCommitMessage.Text = string.Empty;
                await RefreshAsync(repoPath);
            }
            else
            {
                _showError(
                    _getString("GitCommitFailureTitle", "Git 커밋 실패"),
                    _getString("GitCommitFailureMessage", "커밋 도중 에러가 났습니다. 변경 조각(Staged)이 등록되었는지 확인하십시오."));
            }
        }

        public async Task PushAsync(string repoPath)
        {
            bool success = await _gitService.PushAsync(repoPath);
            if (success)
            {
                await RefreshAsync(repoPath);
            }
            else
            {
                _showError(
                    _getString("GitPushFailureTitle", "Git Push 실패"),
                    _getString("GitPushFailureMessage", "Push 처리에 실패했습니다. 원격 저장소/인증/업스트림 설정을 확인하십시오."));
            }
        }

        public async Task StageCommitPushAsync(string repoPath)
        {
            string message = _leftSidebar.GitCommitMessage.Text;
            if (string.IsNullOrEmpty(message))
            {
                _showError(
                    _getString("GitCommitSuccessTitle", "Git 커밋"),
                    _getString("GitCommitEmptyMessage", "커밋 메시지를 채워주십시오."));
                return;
            }

            bool stageSuccess = await _gitService.StageAllAsync(repoPath);
            if (!stageSuccess)
            {
                _showError(
                    _getString("GitStageFailureTitle", "Git Stage 실패"),
                    _getString("GitStageAllFailureMessage", "전체 Stage 처리에 실패했습니다."));
                return;
            }

            bool commitSuccess = await _gitService.CommitAsync(repoPath, message);
            if (!commitSuccess)
            {
                await RefreshAsync(repoPath);
                _showError(
                    _getString("GitCommitFailureTitle", "Git 커밋 실패"),
                    _getString("GitCommitFailureMessage", "커밋 도중 에러가 났습니다. 변경 조각(Staged)이 등록되었는지 확인하십시오."));
                return;
            }

            _leftSidebar.GitCommitMessage.Text = string.Empty;

            string remoteUrl = await _gitService.GetRemoteUrlAsync(repoPath);
            if (!string.IsNullOrEmpty(remoteUrl))
            {
                bool pushSuccess = await _gitService.PushAsync(repoPath);
                if (!pushSuccess)
                {
                    await RefreshAsync(repoPath);
                    _showError(
                        _getString("GitPushFailureTitle", "Git Push 실패"),
                        _getString("GitPushFailureMessage", "Push 처리에 실패했습니다. 원격 저장소/인증/업스트림 설정을 확인하십시오."));
                    return;
                }
            }

            await RefreshAsync(repoPath);
        }

        public async Task PullAsync(string repoPath)
        {
            bool success = await _gitService.PullAsync(repoPath);
            if (success)
            {
                await RefreshAsync(repoPath);
                _showError(
                    _getString("GitPullSuccessTitle", "Git Pull"),
                    _getString("GitPullSuccessMessage", "Pull이 완료되었습니다."));
            }
            else
            {
                _showError(
                    _getString("GitPullFailureTitle", "Git Pull 실패"),
                    _getString("GitPullFailureMessage", "Pull 처리에 실패했습니다. 원격 저장소/인증/충돌 상태를 확인하십시오."));
            }
        }

        public async Task RebaseAsync(string repoPath)
        {
            bool success = await _gitService.RebaseAsync(repoPath);
            if (success)
            {
                await RefreshAsync(repoPath);
                _showError(
                    _getString("GitRebaseSuccessTitle", "Git Rebase"),
                    _getString("GitRebaseSuccessMessage", "Rebase가 완료되었습니다."));
            }
            else
            {
                _showError(
                    _getString("GitRebaseFailureTitle", "Git Rebase 실패"),
                    _getString("GitRebaseFailureMessage", "Rebase 처리에 실패했습니다. 원격 저장소/인증/충돌 상태를 확인하십시오."));
            }
        }

        public async Task CreateBranchAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
            {
                _showError(
                    _getString("GitCreateBranchTitle", "새 브랜치 생성"),
                    _getString("GitRemoteNoRepoMessage", "Git 저장소를 먼저 선택하거나 생성하세요."));
                return;
            }

            ElementTheme dialogTheme = GetCurrentDialogTheme();
            var nameInput = new TextBox
            {
                PlaceholderText = _getString("GitCreateBranchPlaceholder", "Enter branch name..."),
                Width = 300,
                FontSize = 12,
                RequestedTheme = dialogTheme,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false
            };

            var content = new StackPanel
            {
                Spacing = 8,
                Width = 300,
                RequestedTheme = dialogTheme
            };
            content.Children.Add(new TextBlock
            {
                Text = _getString("GitCreateBranchPlaceholder", "Enter branch name..."),
                FontSize = 11,
                RequestedTheme = dialogTheme,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            content.Children.Add(nameInput);

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("GitCreateBranchTitle", "새 브랜치 생성"),
                Content = content,
                PrimaryButtonText = _getString("GitCreateBranchConfirm", "생성"),
                CloseButtonText = _getString("GitRemoteDialogCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = dialogTheme
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();

            if (result == ContentDialogResult.Primary)
            {
                string branchName = nameInput.Text.Trim();
                if (string.IsNullOrEmpty(branchName))
                {
                    return;
                }

                string output = await _gitService.RunGitCommandAsync(repoPath, $"checkout -b \"{branchName}\"");
                bool success = !output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase);
                if (success)
                {
                    await RefreshAsync(repoPath);
                    _showError(
                        _getString("GitCreateBranchSuccessTitle", "브랜치 생성 성공"),
                        string.Format(_getString("GitCreateBranchSuccessMessage", "'{0}' 브랜치가 생성되고 이동되었습니다."), branchName));
                }
                else
                {
                    _showError(
                        _getString("GitCreateBranchFailureTitle", "브랜치 생성 실패"),
                        string.Format(_getString("GitCreateBranchFailureMessage", "브랜치 생성에 실패했습니다. 오류 메시지: {0}"), output.Trim()));
                }
            }
        }

        public async Task MergeBranchAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
            {
                _showError(
                    _getString("GitMergeTitle", "브랜치 병합 (Merge)"),
                    _getString("GitRemoteNoRepoMessage", "Git 저장소를 먼저 선택하거나 생성하세요."));
                return;
            }

            var branches = await _gitService.GetBranchesAsync(repoPath);

            string mergedOutput = await _gitService.RunGitCommandAsync(repoPath, "branch --no-color --merged");
            var mergedBranches = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(mergedOutput) && !mergedOutput.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            {
                var lines = mergedOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    string cleanedLine = line.TrimStart('*', ' ').Trim();
                    if (!string.IsNullOrEmpty(cleanedLine))
                    {
                        mergedBranches.Add(cleanedLine);
                    }
                }
            }

            var mergeableBranches = new System.Collections.Generic.List<string>();
            foreach (var b in branches)
            {
                string cleaned = b.Trim();
                if (cleaned.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (mergedBranches.Contains(cleaned))
                {
                    continue;
                }

                mergeableBranches.Add(cleaned);
            }

            if (mergeableBranches.Count == 0)
            {
                _showError(
                    _getString("GitMergeTitle", "브랜치 병합 (Merge)"),
                    _getString("GitMergeNoBranchesMessage", "병합할 다른 브랜치가 없습니다."));
                return;
            }

            ElementTheme dialogTheme = GetCurrentDialogTheme();
            var combo = new ComboBox
            {
                Width = 300,
                RequestedTheme = dialogTheme
            };
            foreach (var mb in mergeableBranches)
            {
                combo.Items.Add(mb);
            }
            combo.SelectedIndex = 0;

            var content = new StackPanel
            {
                Spacing = 8,
                Width = 300,
                RequestedTheme = dialogTheme
            };
            content.Children.Add(new TextBlock
            {
                Text = _getString("GitMergeLabel", "현재 브랜치로 병합할 브랜치를 선택하세요:"),
                FontSize = 11,
                RequestedTheme = dialogTheme,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            content.Children.Add(combo);

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("GitMergeTitle", "브랜치 병합 (Merge)"),
                Content = content,
                PrimaryButtonText = _getString("GitMergeConfirm", "병합"),
                CloseButtonText = _getString("GitMergeCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = dialogTheme
            };

            var result = await dialog.ShowAsync();
            _afterDialog?.Invoke();

            if (result == ContentDialogResult.Primary && combo.SelectedItem is string selectedBranch)
            {
                string output = await _gitService.RunGitCommandAsync(repoPath, $"merge \"{selectedBranch}\"");
                bool success = !output.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) && !output.Contains("CONFLICT");
                if (success)
                {
                    await RefreshAsync(repoPath);
                    _showError(
                        _getString("GitMergeSuccessTitle", "브랜치 병합 성공"),
                        string.Format(_getString("GitMergeSuccessMessage", "'{0}' 브랜치가 성공적으로 병합되었습니다."), selectedBranch));
                }
                else
                {
                    await RefreshAsync(repoPath);
                    _showError(
                        _getString("GitMergeFailureTitle", "브랜치 병합 실패"),
                        string.Format(_getString("GitMergeFailureMessage", "병합 중 오류가 발생했거나 충돌이 있습니다. {0}"), output.Trim()));
                }
            }
        }

        public async Task CheckoutBranchAsync(string repoPath, string branchName)
        {
            if (string.IsNullOrEmpty(repoPath) || string.IsNullOrWhiteSpace(branchName))
            {
                return;
            }

            _isCheckingOutBranch = true;
            bool wasEnabled = _leftSidebar.GitBranches.IsEnabled;
            _leftSidebar.GitBranches.IsEnabled = false;
            try
            {
                bool success = await _gitService.CheckoutBranchAsync(repoPath, branchName);
                if (!success)
                {
                    _showError(
                        _getString("GitCheckoutFailureTitle", "Git 브랜치 이동 실패"),
                        _getString("GitCheckoutFailureMessage", "브랜치 이동에 실패했습니다. 커밋되지 않은 변경 사항이나 충돌 상태를 확인하십시오."));
                }
            }
            finally
            {
                try
                {
                    await RefreshAsync(repoPath);
                }
                finally
                {
                    _leftSidebar.GitBranches.IsEnabled = wasEnabled;
                    _isCheckingOutBranch = false;
                }
            }
        }

        public async Task ConfigureRemoteAsync(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
            {
                _showError(
                    _getString("GitRemoteDialogTitle", "Git Remote"),
                    _getString("GitRemoteNoRepoMessage", "Git 저장소를 먼저 선택하거나 생성하세요."));
                return;
            }

            ElementTheme dialogTheme = GetCurrentDialogTheme();
            string currentUrl = await _gitService.GetRemoteUrlAsync(repoPath);
            const double RemoteDialogContentWidth = 420;
            var remoteInput = new TextBox
            {
                Text = currentUrl,
                PlaceholderText = "https://github.com/user/repo.git",
                Width = RemoteDialogContentWidth,
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                RequestedTheme = dialogTheme,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false
            };
            ScrollViewer.SetHorizontalScrollMode(remoteInput, ScrollMode.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(remoteInput, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollMode(remoteInput, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(remoteInput, ScrollBarVisibility.Disabled);

            var content = new StackPanel
            {
                Spacing = 8,
                Width = RemoteDialogContentWidth,
                RequestedTheme = dialogTheme
            };
            content.Children.Add(new TextBlock
            {
                Text = _getString("GitRemoteDialogLabel", "origin remote URL"),
                FontSize = 11,
                RequestedTheme = dialogTheme,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            content.Children.Add(remoteInput);

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("GitRemoteDialogTitle", "Git Remote"),
                Content = content,
                PrimaryButtonText = _getString("GitRemoteDialogConnect", "연결"),
                CloseButtonText = _getString("GitRemoteDialogCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = dialogTheme
            };

            ContentDialogResult result = await dialog.ShowAsync();
            _afterDialog?.Invoke();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string remoteUrl = remoteInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.Contains(' '))
            {
                _showError(
                    _getString("GitRemoteDialogTitle", "Git Remote"),
                    _getString("GitRemoteInvalidUrlMessage", "올바른 GitHub remote URL을 입력하세요."));
                return;
            }

            bool success = await _gitService.SetRemoteUrlAsync(repoPath, remoteUrl);
            if (success)
            {
                _showError(
                    _getString("GitRemoteDialogTitle", "Git Remote"),
                    _getString("GitRemoteSuccessMessage", "origin remote가 연결되었습니다."));
            }
            else
            {
                _showError(
                    _getString("GitRemoteFailureTitle", "Git Remote 실패"),
                    _getString("GitRemoteFailureMessage", "remote URL 연결에 실패했습니다."));
            }
        }

        public async Task RestoreAllAsync(string repoPath)
        {
            bool isDarkTheme = false;
            if (_xamlRootProvider()?.Content is FrameworkElement fe)
            {
                isDarkTheme = fe.ActualTheme == ElementTheme.Dark;
            }

            _beforeDialog?.Invoke();
            var dialog = new ContentDialog
            {
                Title = _getString("GitRestoreAllDialogTitle", "Git 전체 복원"),
                Content = _getString("GitRestoreAllDialogContent", "모든 변경 사항을 복원합니다. Untracked 파일은 삭제됩니다."),
                PrimaryButtonText = _getString("GitRestoreAllDialogConfirm", "전체 복원"),
                CloseButtonText = _getString("GitRestoreFileDialogCancel", "취소"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                _afterDialog?.Invoke();
                return;
            }
            _afterDialog?.Invoke();

            bool success = await _gitService.RestoreAllAsync(repoPath);
            if (success)
            {
                await RefreshAsync(repoPath);
                FileRestored?.Invoke(this, string.Empty);
            }
            else
            {
                _showError(
                    _getString("GitRestoreFailureTitle", "Git Restore 실패"),
                    _getString("GitRestoreAllFailureMessage", "전체 복원 처리에 실패했습니다."));
            }
        }

        public async Task ShowHistoryItemAsync(string repoPath)
        {
            if (_leftSidebar.GitHistory.SelectedItem is not GitHistoryItem historyItem ||
                string.IsNullOrEmpty(repoPath) ||
                string.IsNullOrEmpty(historyItem.CommitHash))
            {
                return;
            }

            string hash = historyItem.CommitHash;
            try
            {
                string commitInfo = await _gitService.RunGitCommandAsync(repoPath, $"show --quiet --format=fuller {hash}");
                var changedFiles = await _gitService.GetCommitChangedFilesAsync(repoPath, hash);
                await ShowCommitDialogAsync(repoPath, hash, commitInfo, changedFiles);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("GitCommitDetailsFailureTitle", "커밋 상세 조회 실패"),
                    ex.Message);
            }
        }

        private async Task ShowCommitDialogAsync(
            string repoPath,
            string hash,
            string commitInfo,
            System.Collections.Generic.IReadOnlyList<(string Status, string Path)> changedFiles)
        {
            bool isDarkTheme = false;
            if (_xamlRootProvider()?.Content is FrameworkElement fe)
            {
                isDarkTheme = fe.ActualTheme == ElementTheme.Dark;
            }

            var dialog = new ContentDialog
            {
                Title = string.Format(_getString("GitHistoryItemDialogTitle", "커밋 정보 [{0}]"), hash.Substring(0, 7)),
                CloseButtonText = _getString("GitHistoryItemClose", "닫기"),
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };

            string currentHash = hash;
            var fileListView = BuildCommitChangedFilesList(
                changedFiles,
                isDarkTheme,
                async file =>
                {
                    dialog.Hide();
                    await OpenCommitFileCompareAsync(repoPath, currentHash, file.Status, file.Path);
                });

            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new ScrollViewer
            {
                MaxHeight = 130,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 8),
                Content = new TextBlock
                {
                    Text = commitInfo,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                }
            });
            stack.Children.Add(new TextBlock
            {
                Text = _getString("GitHistoryItemDialogHeader", "변경된 파일 목록 (클릭 시 비교 뷰어 열림):"),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 0, 5)
            });
            stack.Children.Add(fileListView);

            dialog.Content = stack;
            _beforeDialog?.Invoke();
            await dialog.ShowAsync();
            _afterDialog?.Invoke();
        }

        private ListView BuildCommitChangedFilesList(
            System.Collections.Generic.IEnumerable<(string Status, string Path)> changedFiles,
            bool isDarkTheme,
            Func<(string Status, string Path), Task> openFileAsync)
        {
            var fileListView = new ListView
            {
                Height = 220,
                SelectionMode = ListViewSelectionMode.Single,
                Margin = new Thickness(0, 5, 0, 0)
            };

            foreach (var file in changedFiles)
            {
                var listViewItem = new ListViewItem
                {
                    Content = CreateCommitFileRow(file.Status, file.Path, isDarkTheme),
                    Tag = file
                };

                listViewItem.Tapped += async (_, args) =>
                {
                    args.Handled = true;
                    await openFileAsync(file);
                };

                fileListView.Items.Add(listViewItem);
            }

            return fileListView;
        }

        private static Grid CreateCommitFileRow(string status, string path, bool isDarkTheme)
        {
            Windows.UI.Color statusColor;
            if (isDarkTheme)
            {
                // Soft, desaturated premium colors for Dark Theme (GitHub style)
                statusColor = status.StartsWith("A", StringComparison.OrdinalIgnoreCase)
                    ? Windows.UI.Color.FromArgb(255, 63, 185, 80)    // soft green (#3fb950)
                    : status.StartsWith("D", StringComparison.OrdinalIgnoreCase)
                        ? Windows.UI.Color.FromArgb(255, 248, 81, 73)   // soft red (#f85149)
                        : Windows.UI.Color.FromArgb(255, 88, 166, 255);  // soft blue (#58a6ff)
            }
            else
            {
                // Harmonious professional colors for Light Theme
                statusColor = status.StartsWith("A", StringComparison.OrdinalIgnoreCase)
                    ? Windows.UI.Color.FromArgb(255, 34, 134, 58)    // desaturated dark green (#22863a)
                    : status.StartsWith("D", StringComparison.OrdinalIgnoreCase)
                        ? Windows.UI.Color.FromArgb(255, 203, 36, 49)   // desaturated dark red (#cb2431)
                        : Windows.UI.Color.FromArgb(255, 3, 102, 214);   // elegant blue (#0366d6)
            }

            var grid = new Grid { Padding = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var border = new Border
            {
                Background = new SolidColorBrush(statusColor),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = status,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                }
            };

            var pathText = new TextBlock
            {
                Text = path,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(border, 0);
            Grid.SetColumn(pathText, 1);
            grid.Children.Add(border);
            grid.Children.Add(pathText);
            return grid;
        }

        private async Task OpenCommitFileCompareAsync(string repoPath, string currentHash, string status, string relativePath)
        {
            string fullPath = Path.Combine(repoPath, relativePath);
            string parentHash = $"{currentHash}^";
            string contentA = string.Empty;
            string contentB = string.Empty;

            if (!status.StartsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                contentA = await _gitService.GetCommitFileContentAsync(repoPath, parentHash, relativePath);
            }

            if (!status.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            {
                contentB = await _gitService.GetCommitFileContentAsync(repoPath, currentHash, relativePath);
            }

            string fileName = GetDisplayName(relativePath);
            string shortHash = currentHash.Substring(0, 7);
            await _openCompareTabAsync(
                fullPath,
                fullPath,
                contentA,
                contentB,
                string.Format(_getString("GitCompareTitleFormat", "비교 [{0}]: {1}"), shortHash, fileName),
                string.Format(_getString("GitPreviousCommitFormat", "{0} (이전 커밋)"), fileName),
                string.Format(_getString("GitCommitHashFormat", "{0} (커밋 {1})"), fileName, shortHash));
        }

        private static GitFileItem CreateGitFileItem(string fullPath, string status)
        {
            bool isStaged = status.Length > 0 && status[0] != ' ' && status != "??";
            bool isUnstaged = status.Length > 1 && status[1] != ' ';
            string statusDesc = isStaged ? "Staged" : "Unstaged";
            if (status == "??") statusDesc = "Untracked";
            else if (status.Contains("D")) statusDesc = isStaged ? "Deleted staged" : "Deleted";
            else if (status.Contains("R")) statusDesc = "Renamed";
            else if (status.Contains("A")) statusDesc = isStaged ? "Added staged" : "Added";
            else if (isStaged && isUnstaged) statusDesc = "Staged + Unstaged";

            return new GitFileItem
            {
                Name = GetDisplayName(fullPath),
                Path = fullPath,
                StatusText = $"{statusDesc} ({status.Trim()})",
                ActionGlyph = isStaged ? "\xE108" : "\xE109",
                IsStaged = isStaged
            };
        }

        private static string GetDisplayName(string path)
        {
            string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(normalizedPath);
            return string.IsNullOrWhiteSpace(name) ? normalizedPath : name;
        }

        private ElementTheme GetCurrentDialogTheme()
        {
            if (_leftSidebar.ActualTheme is ElementTheme.Light or ElementTheme.Dark)
            {
                return _leftSidebar.ActualTheme;
            }

            if (_xamlRootProvider()?.Content is FrameworkElement fe &&
                fe.ActualTheme is ElementTheme.Light or ElementTheme.Dark)
            {
                return fe.ActualTheme;
            }

            return ElementTheme.Default;
        }

        private void WireEvents()
        {
            _leftSidebar.GitFileItemClick += OnGitFileItemClick;
            _leftSidebar.GitStageToggleClick += OnGitStageToggleClick;
            _leftSidebar.GitRestoreFileClick += OnGitRestoreFileClick;
            _leftSidebar.GitCommitClick += OnGitCommitClick;
            _leftSidebar.GitStageAllClick += OnGitStageAllClick;
            _leftSidebar.GitRestoreAllClick += OnGitRestoreAllClick;
            _leftSidebar.GitPushClick += OnGitPushClick;
            _leftSidebar.GitPullClick += OnGitPullClick;
            _leftSidebar.GitRebaseClick += OnGitRebaseClick;
            _leftSidebar.GitCreateBranchClick += OnGitCreateBranchClick;
            _leftSidebar.GitMergeClick += OnGitMergeClick;
            _leftSidebar.GitRemoteClick += OnGitRemoteClick;
            _leftSidebar.GitScpClick += OnGitScpClick;
            _leftSidebar.GitRefreshClick += OnGitRefreshClick;
            _leftSidebar.GitBranchSelectionChanged += OnGitBranchSelectionChanged;
            _leftSidebar.GitHistoryScrolledToEnd += OnGitHistoryScrolledToEnd;
            _leftSidebar.GitHistoryItemClick += OnGitHistoryItemClick;
            _leftSidebar.GitInitRepoClick += OnGitInitRepoClick;
        }

        private async void OnGitStageAllClick(object sender, RoutedEventArgs e)
        {
            await StageAllAsync(_repoPathProvider());
        }

        private async void OnGitStageToggleClick(object sender, RoutedEventArgs e)
        {
            await ToggleStageAsync(sender, _repoPathProvider());
        }

        private async void OnGitFileItemClick(object sender, ItemClickEventArgs e)
        {
            _leftSidebar.GitChangedFiles.SelectedItem = e.ClickedItem;
            await OpenChangedFileDiffAsync(_repoPathProvider());
        }

        private async void OnGitRestoreFileClick(object sender, RoutedEventArgs e)
        {
            await RestoreFileAsync(sender, _repoPathProvider());
        }

        private async void OnGitCommitClick(object sender, RoutedEventArgs e)
        {
            await CommitAsync(_repoPathProvider());
        }

        private async void OnGitPushClick(object sender, RoutedEventArgs e)
        {
            await PushAsync(_repoPathProvider());
        }

        private async void OnGitPullClick(object sender, RoutedEventArgs e)
        {
            await PullAsync(_repoPathProvider());
        }

        private async void OnGitRebaseClick(object sender, RoutedEventArgs e)
        {
            await RebaseAsync(_repoPathProvider());
        }

        private async void OnGitCreateBranchClick(object sender, RoutedEventArgs e)
        {
            await CreateBranchAsync(_repoPathProvider());
        }

        private async void OnGitMergeClick(object sender, RoutedEventArgs e)
        {
            await MergeBranchAsync(_repoPathProvider());
        }

        private async void OnGitRemoteClick(object sender, RoutedEventArgs e)
        {
            await ConfigureRemoteAsync(_repoPathProvider());
        }

        private async void OnGitScpClick(object sender, RoutedEventArgs e)
        {
            await StageCommitPushAsync(_repoPathProvider());
        }

        private async void OnGitRestoreAllClick(object sender, RoutedEventArgs e)
        {
            await RestoreAllAsync(_repoPathProvider());
        }

        private async void OnGitRefreshClick(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async void OnGitBranchSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingBranchList || _isCheckingOutBranch)
            {
                return;
            }

            if (_leftSidebar.GitBranches.SelectedItem is not string branchDisplay ||
                IsCurrentBranchDisplay(branchDisplay))
            {
                return;
            }

            string branchName = GetBranchNameFromDisplay(branchDisplay);
            if (string.IsNullOrEmpty(branchName))
            {
                return;
            }

            await CheckoutBranchAsync(_repoPathProvider(), branchName);
        }

        private async void OnGitHistoryScrolledToEnd(object? sender, EventArgs e)
        {
            await LoadMoreHistoryAsync(_repoPathProvider());
        }

        private async void OnGitHistoryItemClick(object sender, ItemClickEventArgs e)
        {
            _leftSidebar.GitHistory.SelectedItem = e.ClickedItem;
            await ShowHistoryItemAsync(_repoPathProvider());
        }

        private static bool IsCurrentBranchDisplay(string branchDisplay)
        {
            return branchDisplay.TrimStart().StartsWith("*", StringComparison.Ordinal);
        }

        private static string GetBranchNameFromDisplay(string branchDisplay)
        {
            string trimmed = branchDisplay.Trim();
            return trimmed.StartsWith("*", StringComparison.Ordinal)
                ? trimmed.Substring(1).Trim()
                : trimmed;
        }

        private void ResetGitHistoryPaging()
        {
            _gitHistoryRepoPath = string.Empty;
            _loadedGitHistoryCount = 0;
            _hasMoreGitHistory = false;
            _isLoadingMoreGitHistory = false;
        }

        private async void OnGitInitRepoClick(object sender, RoutedEventArgs e)
        {
            string targetPath = _folderPathProvider();
            if (string.IsNullOrEmpty(targetPath))
            {
                _showError(
                    _getString("GitInitTitle", "Git 초기화"),
                    _getString("GitInitSelectFolderMessage", "탐색기에서 폴더를 먼저 선택하세요."));
                return;
            }

            bool success = await _gitService.InitRepositoryAsync(targetPath);
            if (success)
            {
                await RefreshAsync(targetPath);
            }
            else
            {
                _showError(
                    _getString("GitInitFailureTitle", "Git 초기화 실패"),
                    _getString("GitInitFailureMessage", "Git 저장소 생성에 실패했습니다."));
            }
        }
    }
}
