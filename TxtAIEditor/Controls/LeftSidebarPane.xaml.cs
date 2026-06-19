using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace TxtAIEditor.Controls
{
    public sealed partial class LeftSidebarPane : UserControl
    {
        public static readonly DependencyProperty ReplaceOneTooltipProperty =
            DependencyProperty.Register(
                nameof(ReplaceOneTooltip),
                typeof(string),
                typeof(LeftSidebarPane),
                new PropertyMetadata("이 항목만 바꾸기"));

        public string ReplaceOneTooltip
        {
            get => (string)GetValue(ReplaceOneTooltipProperty);
            set => SetValue(ReplaceOneTooltipProperty, value);
        }

        public static readonly DependencyProperty SnippetEditTooltipProperty =
            DependencyProperty.Register(
                nameof(SnippetEditTooltip),
                typeof(string),
                typeof(LeftSidebarPane),
                new PropertyMetadata("수정"));

        public string SnippetEditTooltip
        {
            get => (string)GetValue(SnippetEditTooltipProperty);
            set => SetValue(SnippetEditTooltipProperty, value);
        }

        public static readonly DependencyProperty SnippetDeleteTooltipProperty =
            DependencyProperty.Register(
                nameof(SnippetDeleteTooltip),
                typeof(string),
                typeof(LeftSidebarPane),
                new PropertyMetadata("삭제"));

        public string SnippetDeleteTooltip
        {
            get => (string)GetValue(SnippetDeleteTooltipProperty);
            set => SetValue(SnippetDeleteTooltipProperty, value);
        }

        public LeftSidebarPane()
        {
            InitializeComponent();
        }

        public event RoutedEventHandler? LeftActivityClick;
        public event RoutedEventHandler? ExplorerUpClick;
        public event RoutedEventHandler? SelectFolderClick;
        public event RoutedEventHandler? CreateFolderClick;
        public event RoutedEventHandler? RefreshClick;
        public event RoutedEventHandler? SortClick;
        public event RoutedEventHandler? OpenInWindowsExplorerClick;
        public event RoutedEventHandler? ExplorerHomeClick;
        public event ItemClickEventHandler? FileListViewItemClick;
        public event RightTappedEventHandler? FileListViewItemRightTapped;
        public event RoutedEventHandler? AddFileToFavoritesClick;
        public event RoutedEventHandler? AddFolderToFavoritesClick;
        public event RoutedEventHandler? InsertMarkdownImageClick;
        public event RoutedEventHandler? CopyFileNameClick;
        public event RoutedEventHandler? CopyFilePathClick;
        public event RoutedEventHandler? CopyFolderPathClick;
        public event RoutedEventHandler? RenameClick;
        public event RoutedEventHandler? DeleteClick;
        public event ItemClickEventHandler? FavoriteItemClick;
        public event RoutedEventHandler? RemoveFavoriteClick;
        public event RoutedEventHandler? FavoritePinClick;
        public event RoutedEventHandler? FavoritesTabClick;
        public event DoubleTappedEventHandler? SnippetItemDoubleTapped;
        public event RoutedEventHandler? DeleteSnippetClick;
        public event RoutedEventHandler? EditSnippetClick;
        public event RoutedEventHandler? AddSnippetClick;
        public event RoutedEventHandler? ExportSnippetsClick;
        public event RoutedEventHandler? ImportSnippetsClick;
        public event RoutedEventHandler? ResetSnippetsClick;
        public event ItemClickEventHandler? GitFileItemClick;
        public event RoutedEventHandler? GitStageToggleClick;
        public event RoutedEventHandler? GitRestoreFileClick;
        public event RoutedEventHandler? GitCommitClick;
        public event RoutedEventHandler? GitStageAllClick;
        public event RoutedEventHandler? GitRestoreAllClick;
        public event RoutedEventHandler? GitPushClick;
        public event RoutedEventHandler? GitPullClick;
        public event RoutedEventHandler? GitRebaseClick;
        public event RoutedEventHandler? GitRemoteClick;
        public event RoutedEventHandler? GitRefreshClick;
        public event ItemClickEventHandler? GitHistoryItemClick;
        public event RoutedEventHandler? GitInitRepoClick;
        public event KeyEventHandler? SearchQueryInputKeyDown;
        public event RoutedEventHandler? SearchAllFilesClick;
        public event RoutedEventHandler? ReplaceAllClick;
        public event RoutedEventHandler? ReplaceOneClick;
        public event ItemClickEventHandler? SearchResultItemClick;
        public event ItemClickEventHandler? RecentFileItemClick;
        public event RoutedEventHandler? RemoveRecentFileClick;
        public event ItemClickEventHandler? TocItemClick;
        public event TextChangedEventHandler? ExplorerFilterTextChanged;

        public Grid ExplorerPage => ExplorerSidebarPage;
        public Grid FavoritesPage => FavoritesSidebarPage;
        public Grid SnippetsPage => SnippetsSidebarPage;
        public Grid GitPage => GitSidebarPage;
        public Grid SearchPage => SearchSidebarPage;
        public Grid RecentPage => RecentSidebarPage;
        public Grid TocPage => TocSidebarPage;

        public ToggleButton ExplorerActivity => ExplorerActivityButton;
        public ToggleButton FavoritesActivity => FavoritesActivityButton;
        public ToggleButton SnippetsActivity => SnippetsActivityButton;
        public ToggleButton GitActivity => GitActivityButton;
        public ToggleButton SearchActivity => SearchActivityButton;
        public ToggleButton RecentActivity => RecentActivityButton;
        public ToggleButton TocActivity => TocActivityButton;

        public TextBlock ExplorerStatus => ExplorerStatusText;
        public TextBlock FavoritesHeader => FavoritesHeaderText;
        public TextBlock SnippetsHeader => SnippetsHeaderText;
        public Button AddSnippet => AddSnippetButton;
        public Button ExportSnippets => ExportSnippetsButton;
        public Button ImportSnippets => ImportSnippetsButton;
        public Button ResetSnippets => ResetSnippetsButton;

        public TextBlock SearchHeaderLabel => SearchHeaderText;
        public Button SearchAllFilesBtn => SearchAllButton;
        public Button ReplaceAllFilesBtn => ReplaceAllButton;
        public TextBlock RecentFilesHeaderLabel => RecentFilesHeaderText;
        public TextBlock GitHeaderLabel => GitHeaderText;
        public Button GitCommitBtn => GitCommitButton;
        public Button GitStageAllBtn => GitStageAllButton;
        public Button GitRestoreAllBtn => GitRestoreAllButton;
        public SplitButton GitPushBtn => GitPushButton;
        public Button GitRemoteBtn => GitRemoteButton;
        public Button GitRefreshBtn => GitRefreshButton;
        public TextBlock GitHistoryHeaderLabel => GitHistoryHeader;
        public Button ExplorerUpBtn => ExplorerUpButton;
        public Button ExplorerSelectFolderBtn => ExplorerSelectFolderButton;
        public Button ExplorerCreateFolderBtn => ExplorerCreateFolderButton;
        public Button ExplorerRefreshBtn => ExplorerRefreshButton;
        public Button ExplorerSortBtn => ExplorerSortButton;
        public Button ExplorerOpenInWindowsBtn => ExplorerOpenInWindowsButton;
        public Button ExplorerHomeBtn => ExplorerHomeButton;

        public ListView FileList => FileListView;
        public ListView FavoritesList => FavoritesListView;
        public ListView RecentFilesList => RecentFilesListView;
        public ListView SnippetsList => SnippetsListView;
        public ListView GitChangedFiles => GitChangedFilesList;
        public ListView GitHistory => GitHistoryList;
        public ListView SearchResults => SearchResultsList;
        public ListView TocList => TocListView;

        public TextBlock GitPanelBranch => GitPanelBranchText;
        public Button GitInitRepoBtn => GitInitRepoButton;
        public ComboBox GitBranches => GitBranchesCombo;
        public TextBox GitCommitMessage => GitCommitMessageInput;

        public TextBox SearchQuery => SearchQueryInput;
        public TextBox ReplaceQuery => ReplaceQueryInput;
        public ToggleButton SearchMatchCase => SearchMatchCaseToggle;
        public ToggleButton SearchWholeWord => SearchWholeWordToggle;
        public ToggleButton SearchRegex => SearchRegexToggle;

        public ToggleButton FavoritesFileTabButton => FavoritesFileTab;
        public ToggleButton FavoritesFolderTabButton => FavoritesFolderTab;

        public void Localize(Func<string, string, string> getString, bool updateEmptyFolderStatus, Func<string, bool> isGitNotDetected)
        {
            ToolTipService.SetToolTip(ExplorerActivityButton, getString("Explorer", "탐색기"));
            ToolTipService.SetToolTip(FavoritesActivityButton, getString("Favorites", "즐겨찾기"));
            ToolTipService.SetToolTip(SnippetsActivityButton, getString("Snippets", "스니펫"));
            ToolTipService.SetToolTip(GitActivityButton, getString("Git", "Git"));
            ToolTipService.SetToolTip(SearchActivityButton, getString("Search", "검색"));
            ToolTipService.SetToolTip(RecentActivityButton, getString("RecentFiles", "최근 파일"));
            ToolTipService.SetToolTip(TocActivityButton, getString("TOC", "목차 (TOC)"));

            if (updateEmptyFolderStatus)
            {
                ExplorerStatusText.Text = getString("NoFolderSelected", "폴더를 선택하세요.");
            }

            FavoritesHeaderText.Text = getString("FavoritesHeader", "즐겨찾기 목록");
            SnippetsHeaderText.Text = getString("SnippetsHeader", "코드 및 수식 템플릿");
            TocHeaderText.Text = getString("TOCHeader", "목차 (TOC)");
            AddSnippetButton.Content = getString("AddSnippet", "스니펫 추가...");
            ExportSnippetsButton.Content = getString("SnippetExport", "내보내기");
            ImportSnippetsButton.Content = getString("SnippetImport", "가져오기");
            ResetSnippetsButton.Content = getString("SnippetReset", "초기화");
            SnippetEditTooltip = getString("SnippetEditTooltip", "수정");
            SnippetDeleteTooltip = getString("SnippetDeleteTooltip", "삭제");

            ToolTipService.SetToolTip(ExplorerUpButton, getString("ExplorerUpTooltip", "상위 폴더"));
            var selectFolderText = getString("ExplorerSelectFolder", "폴더 선택...");
            ToolTipService.SetToolTip(ExplorerSelectFolderButton, selectFolderText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerSelectFolderButton, selectFolderText);
            ToolTipService.SetToolTip(ExplorerCreateFolderButton, getString("ExplorerCreateFolderTooltip", "새 폴더"));
            ToolTipService.SetToolTip(ExplorerRefreshButton, getString("ExplorerRefreshTooltip", "새로고침"));
            ToolTipService.SetToolTip(ExplorerSortButton, getString("ExplorerSortName", "이름순 정렬"));
            var openInWindowsExplorerText = getString("ExplorerOpenInWindowsTooltip", "Windows 탐색기에서 열기");
            ToolTipService.SetToolTip(ExplorerOpenInWindowsButton, openInWindowsExplorerText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerOpenInWindowsButton, openInWindowsExplorerText);

            ExplorerFilterBox.PlaceholderText = getString("ExplorerFilterPlaceholder", "파일명 필터 (하위 폴더 포함)...");

            var homeFolderText = getString("ExplorerHomeFolderTooltip", "홈 폴더로 이동");
            ToolTipService.SetToolTip(ExplorerHomeButton, homeFolderText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerHomeButton, homeFolderText);

            FavoritesFileTab.Content = getString("FavoritesFileTab", "파일");
            FavoritesFolderTab.Content = getString("FavoritesFolderTab", "폴더");

            RecentFilesHeaderText.Text = getString("RecentFilesHeader", "최근 파일");

            SearchHeaderText.Text = getString("SearchHeader", "폴더 전체 검색 및 바꾸기");
            SearchQueryInput.PlaceholderText = getString("SearchPlaceholder", "검색어 입력...");
            ReplaceQueryInput.PlaceholderText = getString("ReplacePlaceholder", "바꿀 단어 입력...");
            ToolTipService.SetToolTip(SearchMatchCaseToggle, getString("SearchMatchCaseTooltip", "대소문자 구분"));
            ToolTipService.SetToolTip(SearchWholeWordToggle, getString("SearchWholeWordTooltip", "단어 단위"));
            ToolTipService.SetToolTip(SearchRegexToggle, getString("SearchRegexTooltip", "정규식 검색"));
            SearchAllButton.Content = getString("SearchAllFiles", "전체 검색");
            ReplaceAllButton.Content = getString("ReplaceAllFiles", "모두 바꾸기");
            ReplaceOneTooltip = getString("SearchReplaceOneTooltip", "이 항목만 바꾸기");

            if (Resources.TryGetValue("LocBridge", out var bridgeObj) && bridgeObj is LocalizationBridge bridge)
            {
                bridge.SnippetEditTooltip = SnippetEditTooltip;
                bridge.SnippetDeleteTooltip = SnippetDeleteTooltip;
                bridge.ReplaceOneTooltip = ReplaceOneTooltip;
            }

            GitHeaderText.Text = getString("GitRepoHeader", "Git 저장소 관리");
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
            GitRemoteButton.Content = getString("GitRemote", "Remote");
            var gitRefreshText = getString("GitRefresh", "새로고침");
            GitRefreshButton.Content = new FontIcon { Glyph = "\xE72C", FontSize = 10 };
            ToolTipService.SetToolTip(GitRefreshButton, gitRefreshText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GitRefreshButton, gitRefreshText);
            GitHistoryHeader.Text = getString("GitHistory", "과거 기록");

            if (isGitNotDetected(GitPanelBranchText.Text))
            {
                GitPanelBranchText.Text = getString("GitNotDetected", "Git: 감지 안됨");
            }
        }

        public int ShowPage(int index)
        {
            Grid[] pages =
            {
                ExplorerSidebarPage,
                FavoritesSidebarPage,
                RecentSidebarPage,
                SearchSidebarPage,
                GitSidebarPage,
                SnippetsSidebarPage,
                TocSidebarPage
            };

            ToggleButton[] buttons =
            {
                ExplorerActivityButton,
                FavoritesActivityButton,
                RecentActivityButton,
                SearchActivityButton,
                GitActivityButton,
                SnippetsActivityButton,
                TocActivityButton
            };

            int safeIndex = Math.Clamp(index, 0, pages.Length - 1);
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visibility = i == safeIndex ? Visibility.Visible : Visibility.Collapsed;
                buttons[i].IsChecked = i == safeIndex;
            }

            return safeIndex;
        }

        private void OnLeftActivityClick(object sender, RoutedEventArgs e) => LeftActivityClick?.Invoke(sender, e);
        private void OnExplorerUpClick(object sender, RoutedEventArgs e) => ExplorerUpClick?.Invoke(sender, e);
        private void OnSelectFolderClick(object sender, RoutedEventArgs e) => SelectFolderClick?.Invoke(sender, e);
        private void OnCreateFolderClick(object sender, RoutedEventArgs e) => CreateFolderClick?.Invoke(sender, e);
        private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshClick?.Invoke(sender, e);
        private void OnSortClick(object sender, RoutedEventArgs e) => SortClick?.Invoke(sender, e);
        private void OnOpenInWindowsExplorerClick(object sender, RoutedEventArgs e) => OpenInWindowsExplorerClick?.Invoke(sender, e);
        private void OnExplorerHomeClick(object sender, RoutedEventArgs e) => ExplorerHomeClick?.Invoke(sender, e);
        private void OnFileListViewItemClick(object sender, ItemClickEventArgs e) => FileListViewItemClick?.Invoke(sender, e);
        private void OnFileListViewItemRightTapped(object sender, RightTappedRoutedEventArgs e) => FileListViewItemRightTapped?.Invoke(sender, e);
        private void OnAddFileToFavoritesClick(object sender, RoutedEventArgs e) => AddFileToFavoritesClick?.Invoke(sender, e);
        private void OnAddFolderToFavoritesClick(object sender, RoutedEventArgs e) => AddFolderToFavoritesClick?.Invoke(sender, e);
        private void OnInsertMarkdownImageClick(object sender, RoutedEventArgs e) => InsertMarkdownImageClick?.Invoke(sender, e);
        private void OnCopyFileNameClick(object sender, RoutedEventArgs e) => CopyFileNameClick?.Invoke(sender, e);
        private void OnCopyFilePathClick(object sender, RoutedEventArgs e) => CopyFilePathClick?.Invoke(sender, e);
        private void OnCopyFolderPathClick(object sender, RoutedEventArgs e) => CopyFolderPathClick?.Invoke(sender, e);
        private void OnRenameClick(object sender, RoutedEventArgs e) => RenameClick?.Invoke(sender, e);
        private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteClick?.Invoke(sender, e);
        private void OnFavoriteItemClick(object sender, ItemClickEventArgs e) => FavoriteItemClick?.Invoke(sender, e);
        private void OnRemoveFavoriteClick(object sender, RoutedEventArgs e) => RemoveFavoriteClick?.Invoke(sender, e);
        private void OnFavoritePinClick(object sender, RoutedEventArgs e) => FavoritePinClick?.Invoke(sender, e);
        private void OnFavoritesTabClick(object sender, RoutedEventArgs e) => FavoritesTabClick?.Invoke(sender, e);
        private void OnSnippetItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => SnippetItemDoubleTapped?.Invoke(sender, e);
        private void OnDeleteSnippetClick(object sender, RoutedEventArgs e) => DeleteSnippetClick?.Invoke(sender, e);
        private void OnEditSnippetClick(object sender, RoutedEventArgs e) => EditSnippetClick?.Invoke(sender, e);
        private void OnAddSnippetClick(object sender, RoutedEventArgs e) => AddSnippetClick?.Invoke(sender, e);
        private void OnExportSnippetsClick(object sender, RoutedEventArgs e) => ExportSnippetsClick?.Invoke(sender, e);
        private void OnImportSnippetsClick(object sender, RoutedEventArgs e) => ImportSnippetsClick?.Invoke(sender, e);
        private void OnResetSnippetsClick(object sender, RoutedEventArgs e) => ResetSnippetsClick?.Invoke(sender, e);
        private void OnGitFileItemClick(object sender, ItemClickEventArgs e) => GitFileItemClick?.Invoke(sender, e);
        private void OnGitStageToggleClick(object sender, RoutedEventArgs e) => GitStageToggleClick?.Invoke(sender, e);
        private void OnGitRestoreFileClick(object sender, RoutedEventArgs e) => GitRestoreFileClick?.Invoke(sender, e);
        private void OnGitCommitClick(object sender, RoutedEventArgs e) => GitCommitClick?.Invoke(sender, e);
        private void OnGitStageAllClick(object sender, RoutedEventArgs e) => GitStageAllClick?.Invoke(sender, e);
        private void OnGitRestoreAllClick(object sender, RoutedEventArgs e) => GitRestoreAllClick?.Invoke(sender, e);
        private void OnGitPushClick(SplitButton sender, SplitButtonClickEventArgs e) => GitPushClick?.Invoke(sender, new RoutedEventArgs());
        private void OnGitPullClick(object sender, RoutedEventArgs e) => GitPullClick?.Invoke(sender, e);
        private void OnGitRebaseClick(object sender, RoutedEventArgs e) => GitRebaseClick?.Invoke(sender, e);
        private void OnGitRemoteClick(object sender, RoutedEventArgs e) => GitRemoteClick?.Invoke(sender, e);
        private void OnGitRefreshClick(object sender, RoutedEventArgs e) => GitRefreshClick?.Invoke(sender, e);
        private void OnGitHistoryItemClick(object sender, ItemClickEventArgs e) => GitHistoryItemClick?.Invoke(sender, e);
        private void OnGitInitRepoClick(object sender, RoutedEventArgs e) => GitInitRepoClick?.Invoke(sender, e);
        private void OnSearchQueryInputKeyDown(object sender, KeyRoutedEventArgs e) => SearchQueryInputKeyDown?.Invoke(sender, e);
        private void OnSearchAllFilesClick(object sender, RoutedEventArgs e) => SearchAllFilesClick?.Invoke(sender, e);
        private void OnReplaceAllClick(object sender, RoutedEventArgs e) => ReplaceAllClick?.Invoke(sender, e);
        private void OnReplaceOneClick(object sender, RoutedEventArgs e) => ReplaceOneClick?.Invoke(sender, e);
        private void OnSearchResultItemClick(object sender, ItemClickEventArgs e) => SearchResultItemClick?.Invoke(sender, e);
        private void OnRecentFileItemClick(object sender, ItemClickEventArgs e) => RecentFileItemClick?.Invoke(sender, e);
        private void OnRemoveRecentFileClick(object sender, RoutedEventArgs e) => RemoveRecentFileClick?.Invoke(sender, e);
        private void OnTocItemClick(object sender, ItemClickEventArgs e) => TocItemClick?.Invoke(sender, e);
        private void OnExplorerFilterTextChanged(object sender, TextChangedEventArgs e) => ExplorerFilterTextChanged?.Invoke(sender, e);
    }

    public class LocalizationBridge : System.ComponentModel.INotifyPropertyChanged
    {
        private string _snippetEditTooltip = "수정";
        private string _snippetDeleteTooltip = "삭제";
        private string _replaceOneTooltip = "이 항목만 바꾸기";

        public string SnippetEditTooltip
        {
            get => _snippetEditTooltip;
            set
            {
                if (_snippetEditTooltip != value)
                {
                    _snippetEditTooltip = value;
                    OnPropertyChanged(nameof(SnippetEditTooltip));
                }
            }
        }

        public string SnippetDeleteTooltip
        {
            get => _snippetDeleteTooltip;
            set
            {
                if (_snippetDeleteTooltip != value)
                {
                    _snippetDeleteTooltip = value;
                    OnPropertyChanged(nameof(SnippetDeleteTooltip));
                }
            }
        }

        public string ReplaceOneTooltip
        {
            get => _replaceOneTooltip;
            set
            {
                if (_replaceOneTooltip != value)
                {
                    _replaceOneTooltip = value;
                    OnPropertyChanged(nameof(ReplaceOneTooltip));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
}
