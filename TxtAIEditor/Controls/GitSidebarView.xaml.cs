using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed partial class GitSidebarView : UserControl
    {
        private ScrollViewer? _historyScrollViewer;

        public GitSidebarView()
        {
            InitializeComponent();
            GitBranchesCombo.SelectionChanged += OnGitBranchSelectionChanged;
            GitHistoryList.Loaded += OnGitHistoryListLoaded;
        }

        public Grid Root => RootGrid;
        public TextBlock Header => GitHeaderText;
        public TextBlock PanelBranch => GitPanelBranchText;
        public TextBlock RepoPath => GitRepoPathText;
        public Button InitRepoButton => GitInitRepoButton;
        public ComboBox Branches => GitBranchesCombo;
        public TextBox CommitMessage => GitCommitMessageInput;
        public ListView ChangedFiles => GitChangedFilesList;
        public ListView History => GitHistoryList;
        public Button CommitButton => GitCommitButton;
        public Button StageAllButton => GitStageAllButton;
        public Button RestoreAllButton => GitRestoreAllButton;
        public SplitButton PushButton => GitPushButton;
        public Button RemoteButton => GitRemoteButton;
        public Button ScpButton => GitScpButton;
        public Button RefreshButton => GitRefreshButton;
        public TextBlock HistoryHeader => GitHistoryHeader;

        public event ItemClickEventHandler? FileItemClick;
        public event RoutedEventHandler? StageToggleClick;
        public event RoutedEventHandler? RestoreFileClick;
        public event RoutedEventHandler? CommitClick;
        public event RoutedEventHandler? StageAllClick;
        public event RoutedEventHandler? RestoreAllClick;
        public event RoutedEventHandler? PushClick;
        public event RoutedEventHandler? PullClick;
        public event RoutedEventHandler? RebaseClick;
        public event RoutedEventHandler? CreateBranchClick;
        public event RoutedEventHandler? MergeClick;
        public event RoutedEventHandler? GcClick;
        public event RoutedEventHandler? HardResetClick;
        public event RoutedEventHandler? PushForceClick;
        public event RoutedEventHandler? RemoteClick;
        public event RoutedEventHandler? ScpClick;
        public event RoutedEventHandler? RefreshClick;
        public event SelectionChangedEventHandler? BranchSelectionChanged;
        public event EventHandler? HistoryScrolledToEnd;
        public event ItemClickEventHandler? HistoryItemClick;
        public event RoutedEventHandler? InitRepoClick;

        public void Localize(Func<string, string, string> getString)
        {
            GitHeaderText.Text = getString("GitRepoHeader", "Git 저장소 관리");
            var gitRepoPathLabel = getString("GitRepoPathLabel", "경로");
            ToolTipService.SetToolTip(GitRepoPathText, gitRepoPathLabel);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GitRepoPathText, gitRepoPathLabel);
            GitInitRepoButton.Content = getString("GitCreateRepo", "Git 생성");
            ToolTipService.SetToolTip(GitInitRepoButton, getString("GitCreateRepoTooltip", "Git 저장소 생성"));
            GitBranchesCombo.PlaceholderText = getString("GitBranchPlaceholder", "브랜치 목록");
            GitCommitMessageInput.PlaceholderText = getString("GitCommitPlaceholder", "커밋 메시지 입력...");
            GitCommitButton.Content = getString("GitCommit", "커밋 (Commit)");
            GitStageAllButton.Content = getString("GitStageAll", "전체 Stage");
            GitRestoreAllButton.Content = getString("GitRestoreAll", "전체 Restore");
            GitPushButton.Content = getString("GitPush", "Push");
            ToolTipService.SetToolTip(GitPushButton, getString("GitPushMenuTooltip", "Git 작업"));
            GitPullMenuItem.Text = getString("GitPull", "Pull");
            GitRebaseMenuItem.Text = getString("GitRebase", "Rebase");
            GitCreateBranchMenuItem.Text = getString("GitCreateBranch", "Create Branch");
            GitMergeMenuItem.Text = getString("GitMerge", "Merge Branch");
            GitGcMenuItem.Text = "git gc";
            GitHardResetMenuItem.Text = "hard reset";
            GitPushForceMenuItem.Text = "push force";
            GitRemoteButton.Content = getString("GitRemote", "Remote");
            GitScpButton.Content = getString("GitScp", "S/C/P");
            ToolTipService.SetToolTip(GitScpButton, getString("GitScpTooltip", "Stage/Commit/Push"));

            var gitRefreshText = getString("GitRefresh", "새로고침");
            GitRefreshButton.Content = new FontIcon { Glyph = "\xE72C", FontSize = 10 };
            ToolTipService.SetToolTip(GitRefreshButton, gitRefreshText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GitRefreshButton, gitRefreshText);
            GitHistoryHeader.Text = getString("GitHistory", "과거 기록");

            if (Resources.TryGetValue("LocBridge", out var bridgeObject) &&
                bridgeObject is LocalizationBridge bridge)
            {
                bridge.GitRestoreFileTooltip = getString("GitRestoreFile", "파일 복원");
            }

            if (GitBranchStatus.IsNotDetectedTag(GitPanelBranchText.Tag))
            {
                GitPanelBranchText.Text = getString("GitNotDetected", "Git: 감지 안됨");
            }
        }

        private void OnGitFileItemClick(object sender, ItemClickEventArgs e) => FileItemClick?.Invoke(sender, e);
        private void OnGitStageToggleClick(object sender, RoutedEventArgs e) => StageToggleClick?.Invoke(sender, e);
        private void OnGitRestoreFileClick(object sender, RoutedEventArgs e) => RestoreFileClick?.Invoke(sender, e);
        private void OnGitCommitClick(object sender, RoutedEventArgs e) => CommitClick?.Invoke(sender, e);
        private void OnGitStageAllClick(object sender, RoutedEventArgs e) => StageAllClick?.Invoke(sender, e);
        private void OnGitRestoreAllClick(object sender, RoutedEventArgs e) => RestoreAllClick?.Invoke(sender, e);
        private void OnGitPushClick(SplitButton sender, SplitButtonClickEventArgs e) => PushClick?.Invoke(sender, new RoutedEventArgs());
        private void OnGitPullClick(object sender, RoutedEventArgs e) => PullClick?.Invoke(sender, e);
        private void OnGitRebaseClick(object sender, RoutedEventArgs e) => RebaseClick?.Invoke(sender, e);
        private void OnGitCreateBranchClick(object sender, RoutedEventArgs e) => CreateBranchClick?.Invoke(sender, e);
        private void OnGitMergeClick(object sender, RoutedEventArgs e) => MergeClick?.Invoke(sender, e);
        private void OnGitGcClick(object sender, RoutedEventArgs e) => GcClick?.Invoke(sender, e);
        private void OnGitHardResetClick(object sender, RoutedEventArgs e) => HardResetClick?.Invoke(sender, e);
        private void OnGitPushForceClick(object sender, RoutedEventArgs e) => PushForceClick?.Invoke(sender, e);
        private void OnGitRemoteClick(object sender, RoutedEventArgs e) => RemoteClick?.Invoke(sender, e);
        private void OnGitScpClick(object sender, RoutedEventArgs e) => ScpClick?.Invoke(sender, e);
        private void OnGitRefreshClick(object sender, RoutedEventArgs e) => RefreshClick?.Invoke(sender, e);
        private void OnGitBranchSelectionChanged(object sender, SelectionChangedEventArgs e) => BranchSelectionChanged?.Invoke(sender, e);
        private void OnGitHistoryItemClick(object sender, ItemClickEventArgs e) => HistoryItemClick?.Invoke(sender, e);
        private void OnGitInitRepoClick(object sender, RoutedEventArgs e) => InitRepoClick?.Invoke(sender, e);

        private void OnGitHistoryListLoaded(object sender, RoutedEventArgs e)
        {
            if (_historyScrollViewer != null)
            {
                return;
            }

            _historyScrollViewer = FindVisualChild<ScrollViewer>(GitHistoryList);
            if (_historyScrollViewer != null)
            {
                _historyScrollViewer.ViewChanged += OnGitHistoryScrollViewerViewChanged;
            }
            else
            {
                GitHistoryList.LayoutUpdated += OnGitHistoryListLayoutUpdated;
            }
        }

        private void OnGitHistoryListLayoutUpdated(object? sender, object e)
        {
            if (_historyScrollViewer != null)
            {
                GitHistoryList.LayoutUpdated -= OnGitHistoryListLayoutUpdated;
                return;
            }

            _historyScrollViewer = FindVisualChild<ScrollViewer>(GitHistoryList);
            if (_historyScrollViewer != null)
            {
                GitHistoryList.LayoutUpdated -= OnGitHistoryListLayoutUpdated;
                _historyScrollViewer.ViewChanged += OnGitHistoryScrollViewerViewChanged;
            }
        }

        private void OnGitHistoryScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer || scrollViewer.ScrollableHeight <= 0)
            {
                return;
            }

            const double BottomLoadThreshold = 24;
            if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - BottomLoadThreshold)
            {
                HistoryScrolledToEnd?.Invoke(this, EventArgs.Empty);
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent)
            where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                T? descendant = FindVisualChild<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }
    }
}
