using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed partial class LeftSidebarPane : UserControl
    {
        public LeftSidebarPane()
        {
            InitializeComponent();
        }

        public event RoutedEventHandler? LeftActivityClick;
        public event RoutedEventHandler? ExplorerUpClick { add => ExplorerView.UpClick += value; remove => ExplorerView.UpClick -= value; }
        public event RoutedEventHandler? SelectFolderClick { add => ExplorerView.SelectFolderClick += value; remove => ExplorerView.SelectFolderClick -= value; }
        public event RoutedEventHandler? CreateFolderClick { add => ExplorerView.CreateFolderClick += value; remove => ExplorerView.CreateFolderClick -= value; }
        public event RoutedEventHandler? CreateFileClick { add => ExplorerView.CreateFileClick += value; remove => ExplorerView.CreateFileClick -= value; }
        public event RoutedEventHandler? RefreshClick { add => ExplorerView.RefreshClick += value; remove => ExplorerView.RefreshClick -= value; }
        public event RoutedEventHandler? SortClick { add => ExplorerView.SortClick += value; remove => ExplorerView.SortClick -= value; }
        public event EventHandler<RemoteFileOpenedEventArgs>? RemoteFileOpened { add => ExplorerView.RemoteFileOpened += value; remove => ExplorerView.RemoteFileOpened -= value; }
        public event EventHandler<RemoteServerSelectedEventArgs>? RemoteServerSelected { add => ExplorerView.RemoteServerSelected += value; remove => ExplorerView.RemoteServerSelected -= value; }
        public event RoutedEventHandler? OpenInWindowsExplorerClick { add => ExplorerView.OpenInWindowsExplorerClick += value; remove => ExplorerView.OpenInWindowsExplorerClick -= value; }
        public event RoutedEventHandler? ExplorerHomeClick { add => ExplorerView.HomeClick += value; remove => ExplorerView.HomeClick -= value; }
        public event RoutedEventHandler? ExplorerTreeModeClick { add => ExplorerView.TreeModeClick += value; remove => ExplorerView.TreeModeClick -= value; }
        public event EventHandler<TreeViewExpandingEventArgs>? ExplorerTreeExpanding { add => ExplorerView.TreeExpanding += value; remove => ExplorerView.TreeExpanding -= value; }
        public event EventHandler<TreeViewItemInvokedEventArgs>? ExplorerTreeItemInvoked { add => ExplorerView.TreeItemInvoked += value; remove => ExplorerView.TreeItemInvoked -= value; }
        public event DragEventHandler? ExplorerTreeDragOver { add => ExplorerView.TreeDragOver += value; remove => ExplorerView.TreeDragOver -= value; }
        public event DragEventHandler? ExplorerTreeDrop { add => ExplorerView.TreeDrop += value; remove => ExplorerView.TreeDrop -= value; }
        public event ItemClickEventHandler? FileListViewItemClick { add => ExplorerView.FileItemClick += value; remove => ExplorerView.FileItemClick -= value; }
        public event RightTappedEventHandler? FileListViewItemRightTapped { add => ExplorerView.FileItemRightTapped += value; remove => ExplorerView.FileItemRightTapped -= value; }
        public event RoutedEventHandler? AddFileToFavoritesClick { add => ExplorerView.AddFileToFavoritesClick += value; remove => ExplorerView.AddFileToFavoritesClick -= value; }
        public event RoutedEventHandler? AddFolderToFavoritesClick { add => ExplorerView.AddFolderToFavoritesClick += value; remove => ExplorerView.AddFolderToFavoritesClick -= value; }
        public event RoutedEventHandler? InsertMarkdownImageClick { add => ExplorerView.InsertMarkdownImageClick += value; remove => ExplorerView.InsertMarkdownImageClick -= value; }
        public event RoutedEventHandler? OpenExternalViewerClick { add => ExplorerView.OpenExternalViewerClick += value; remove => ExplorerView.OpenExternalViewerClick -= value; }
        public event RoutedEventHandler? OpenWithDefaultProgramClick { add => ExplorerView.OpenWithDefaultProgramClick += value; remove => ExplorerView.OpenWithDefaultProgramClick -= value; }
        public event RoutedEventHandler? ExtractArchiveToFolderClick { add => ExplorerView.ExtractArchiveToFolderClick += value; remove => ExplorerView.ExtractArchiveToFolderClick -= value; }
        public event RoutedEventHandler? CompressFolderToZipClick { add => ExplorerView.CompressFolderToZipClick += value; remove => ExplorerView.CompressFolderToZipClick -= value; }
        public event RoutedEventHandler? CompressFolderToSevenZipClick { add => ExplorerView.CompressFolderToSevenZipClick += value; remove => ExplorerView.CompressFolderToSevenZipClick -= value; }
        public event RoutedEventHandler? DownloadRemoteItemClick { add => ExplorerView.DownloadRemoteItemClick += value; remove => ExplorerView.DownloadRemoteItemClick -= value; }
        public event RoutedEventHandler? CopyFileNameClick { add => ExplorerView.CopyFileNameClick += value; remove => ExplorerView.CopyFileNameClick -= value; }
        public event RoutedEventHandler? CopyFilePathClick { add => ExplorerView.CopyFilePathClick += value; remove => ExplorerView.CopyFilePathClick -= value; }
        public event RoutedEventHandler? CopyFolderPathClick { add => ExplorerView.CopyFolderPathClick += value; remove => ExplorerView.CopyFolderPathClick -= value; }
        public event RoutedEventHandler? RenameClick { add => ExplorerView.RenameClick += value; remove => ExplorerView.RenameClick -= value; }
        public event RoutedEventHandler? DeleteClick { add => ExplorerView.DeleteClick += value; remove => ExplorerView.DeleteClick -= value; }
        public event ItemClickEventHandler? FavoriteItemClick { add => FavoritesView.ItemClick += value; remove => FavoritesView.ItemClick -= value; }
        public event RoutedEventHandler? RemoveFavoriteClick { add => FavoritesView.RemoveClick += value; remove => FavoritesView.RemoveClick -= value; }
        public event RoutedEventHandler? FavoritePinClick { add => FavoritesView.PinClick += value; remove => FavoritesView.PinClick -= value; }
        public event RoutedEventHandler? FavoritesTabClick { add => FavoritesView.TabClick += value; remove => FavoritesView.TabClick -= value; }
        public event RoutedEventHandler? RecentTabClick { add => RecentView.TabClick += value; remove => RecentView.TabClick -= value; }
        public event DoubleTappedEventHandler? SnippetItemDoubleTapped { add => SnippetsView.ItemDoubleTapped += value; remove => SnippetsView.ItemDoubleTapped -= value; }
        public event RoutedEventHandler? DeleteSnippetClick { add => SnippetsView.DeleteClick += value; remove => SnippetsView.DeleteClick -= value; }
        public event RoutedEventHandler? EditSnippetClick { add => SnippetsView.EditClick += value; remove => SnippetsView.EditClick -= value; }
        public event RoutedEventHandler? AddSnippetClick { add => SnippetsView.AddClick += value; remove => SnippetsView.AddClick -= value; }
        public event RoutedEventHandler? ExportSnippetsClick { add => SnippetsView.ExportClick += value; remove => SnippetsView.ExportClick -= value; }
        public event RoutedEventHandler? ImportSnippetsClick { add => SnippetsView.ImportClick += value; remove => SnippetsView.ImportClick -= value; }
        public event RoutedEventHandler? ResetSnippetsClick { add => SnippetsView.ResetClick += value; remove => SnippetsView.ResetClick -= value; }
        public event RoutedEventHandler? AutocompleteDictClick { add => SnippetsView.AutocompleteDictionaryClick += value; remove => SnippetsView.AutocompleteDictionaryClick -= value; }
        public event ItemClickEventHandler? GitFileItemClick { add => GitView.FileItemClick += value; remove => GitView.FileItemClick -= value; }
        public event RoutedEventHandler? GitStageToggleClick { add => GitView.StageToggleClick += value; remove => GitView.StageToggleClick -= value; }
        public event RoutedEventHandler? GitRestoreFileClick { add => GitView.RestoreFileClick += value; remove => GitView.RestoreFileClick -= value; }
        public event RoutedEventHandler? GitCommitClick { add => GitView.CommitClick += value; remove => GitView.CommitClick -= value; }
        public event RoutedEventHandler? GitStageAllClick { add => GitView.StageAllClick += value; remove => GitView.StageAllClick -= value; }
        public event RoutedEventHandler? GitRestoreAllClick { add => GitView.RestoreAllClick += value; remove => GitView.RestoreAllClick -= value; }
        public event RoutedEventHandler? GitPushClick { add => GitView.PushClick += value; remove => GitView.PushClick -= value; }
        public event RoutedEventHandler? GitPullClick { add => GitView.PullClick += value; remove => GitView.PullClick -= value; }
        public event RoutedEventHandler? GitRebaseClick { add => GitView.RebaseClick += value; remove => GitView.RebaseClick -= value; }
        public event RoutedEventHandler? GitCreateBranchClick { add => GitView.CreateBranchClick += value; remove => GitView.CreateBranchClick -= value; }
        public event RoutedEventHandler? GitMergeClick { add => GitView.MergeClick += value; remove => GitView.MergeClick -= value; }
        public event RoutedEventHandler? GitGcClick { add => GitView.GcClick += value; remove => GitView.GcClick -= value; }
        public event RoutedEventHandler? GitHardResetClick { add => GitView.HardResetClick += value; remove => GitView.HardResetClick -= value; }
        public event RoutedEventHandler? GitPushForceClick { add => GitView.PushForceClick += value; remove => GitView.PushForceClick -= value; }
        public event RoutedEventHandler? GitRemoteClick { add => GitView.RemoteClick += value; remove => GitView.RemoteClick -= value; }
        public event RoutedEventHandler? GitScpClick { add => GitView.ScpClick += value; remove => GitView.ScpClick -= value; }
        public event RoutedEventHandler? GitRefreshClick { add => GitView.RefreshClick += value; remove => GitView.RefreshClick -= value; }
        public event SelectionChangedEventHandler? GitBranchSelectionChanged { add => GitView.BranchSelectionChanged += value; remove => GitView.BranchSelectionChanged -= value; }
        public event EventHandler? GitHistoryScrolledToEnd { add => GitView.HistoryScrolledToEnd += value; remove => GitView.HistoryScrolledToEnd -= value; }
        public event ItemClickEventHandler? GitHistoryItemClick { add => GitView.HistoryItemClick += value; remove => GitView.HistoryItemClick -= value; }
        public event RoutedEventHandler? GitInitRepoClick { add => GitView.InitRepoClick += value; remove => GitView.InitRepoClick -= value; }
        public event KeyEventHandler? SearchQueryInputKeyDown { add => SearchView.QueryKeyDown += value; remove => SearchView.QueryKeyDown -= value; }
        public event RoutedEventHandler? SearchAllFilesClick { add => SearchView.SearchAllClick += value; remove => SearchView.SearchAllClick -= value; }
        public event RoutedEventHandler? ReplaceAllClick { add => SearchView.ReplaceAllClick += value; remove => SearchView.ReplaceAllClick -= value; }
        public event RoutedEventHandler? ReplaceOneClick { add => SearchView.ReplaceOneClick += value; remove => SearchView.ReplaceOneClick -= value; }
        public event ItemClickEventHandler? SearchResultItemClick { add => SearchView.ResultItemClick += value; remove => SearchView.ResultItemClick -= value; }
        public event ItemClickEventHandler? RecentFileItemClick { add => RecentView.ItemClick += value; remove => RecentView.ItemClick -= value; }
        public event RoutedEventHandler? RemoveRecentFileClick { add => RecentView.RemoveClick += value; remove => RecentView.RemoveClick -= value; }
        public event ItemClickEventHandler? TocItemClick { add => TocView.ItemClick += value; remove => TocView.ItemClick -= value; }
        public event TextChangedEventHandler? ExplorerFilterTextChanged { add => ExplorerView.FilterTextChanged += value; remove => ExplorerView.FilterTextChanged -= value; }
        public event DragEventHandler? FileListViewDragOver { add => ExplorerView.FileListDragOver += value; remove => ExplorerView.FileListDragOver -= value; }
        public event DragEventHandler? FileListViewDrop { add => ExplorerView.FileListDrop += value; remove => ExplorerView.FileListDrop -= value; }
        public event DragEventHandler? FileListViewItemDragOver { add => ExplorerView.FileItemDragOver += value; remove => ExplorerView.FileItemDragOver -= value; }
        public event DragEventHandler? FileListViewItemDrop { add => ExplorerView.FileItemDrop += value; remove => ExplorerView.FileItemDrop -= value; }

        public Grid ExplorerPage => ExplorerView.Root;
        public Grid FavoritesPage => FavoritesView.Root;
        public Grid SnippetsPage => SnippetsView.Root;
        public Grid GitPage => GitView.Root;
        public Grid SearchPage => SearchView.Root;
        public Grid RecentPage => RecentView.Root;
        public Grid TocPage => TocView.Root;

        public ToggleButton ExplorerActivity => ExplorerActivityButton;
        public ToggleButton FavoritesActivity => FavoritesActivityButton;
        public ToggleButton SnippetsActivity => SnippetsActivityButton;
        public ToggleButton GitActivity => GitActivityButton;
        public ToggleButton SearchActivity => SearchActivityButton;
        public ToggleButton RecentActivity => RecentActivityButton;
        public ToggleButton TocActivity => TocActivityButton;

        public TextBlock ExplorerStatus => ExplorerView.Status;
        public TextBlock FavoritesHeader => FavoritesView.Header;
        public TextBlock SnippetsHeader => SnippetsView.Header;
        public Button AddSnippet => SnippetsView.AddButton;
        public Button ExportSnippets => SnippetsView.ExportButton;
        public Button ImportSnippets => SnippetsView.ImportButton;
        public Button ResetSnippets => SnippetsView.ResetButton;
        public Button AutocompleteDict => SnippetsView.AutocompleteDictionaryButton;

        public TextBlock SearchHeaderLabel => SearchView.Header;
        public FrameworkElement SearchProgressIndicator => SearchView.ProgressIndicator;
        public Button SearchAllFilesBtn => SearchView.SearchAllButtonControl;
        public Button ReplaceAllFilesBtn => SearchView.ReplaceAllButtonControl;
        public TextBlock RecentFilesHeaderLabel => RecentView.Header;
        public TextBlock GitHeaderLabel => GitView.Header;
        public Button GitCommitBtn => GitView.CommitButton;
        public Button GitStageAllBtn => GitView.StageAllButton;
        public Button GitRestoreAllBtn => GitView.RestoreAllButton;
        public SplitButton GitPushBtn => GitView.PushButton;
        public Button GitRemoteBtn => GitView.RemoteButton;
        public Button GitScpBtn => GitView.ScpButton;
        public Button GitRefreshBtn => GitView.RefreshButton;
        public TextBlock GitHistoryHeaderLabel => GitView.HistoryHeader;
        public Button ExplorerUpBtn => ExplorerView.UpButton;
        public Button ExplorerSelectFolderBtn => ExplorerView.SelectFolderButton;
        public Button ExplorerCreateFolderBtn => ExplorerView.CreateFolderButton;
        public Button ExplorerRefreshBtn => ExplorerView.RefreshButton;
        public Button ExplorerSortBtn => ExplorerView.SortButton;
        public Button ExplorerRemoteBtn => ExplorerView.RemoteButton;
        public Button ExplorerOpenInWindowsBtn => ExplorerView.OpenInWindowsButton;
        public Button ExplorerHomeBtn => ExplorerView.HomeButton;
        public ToggleButton ExplorerTreeModeBtn => ExplorerView.TreeModeButton;
        public TreeView ExplorerTree => ExplorerView.Tree;

        public ListView FileList => ExplorerView.FileList;
        public ListView FavoritesList => FavoritesView.Items;
        public ListView RecentFilesList => RecentView.Items;
        public ListView SnippetsList => SnippetsView.Items;
        public ListView GitChangedFiles => GitView.ChangedFiles;
        public ListView GitHistory => GitView.History;
        public ListView SearchResults => SearchView.Results;
        public ListView TocList => TocView.Items;

        public TextBlock GitPanelBranch => GitView.PanelBranch;
        public TextBlock GitRepoPath => GitView.RepoPath;
        public Button GitInitRepoBtn => GitView.InitRepoButton;
        public ComboBox GitBranches => GitView.Branches;
        public TextBox GitCommitMessage => GitView.CommitMessage;

        public TextBox SearchQuery => SearchView.SearchQuery;
        public TextBox ReplaceQuery => SearchView.ReplaceQuery;
        public ToggleButton SearchMatchCase => SearchView.MatchCase;
        public ToggleButton SearchWholeWord => SearchView.WholeWord;
        public ToggleButton SearchRegex => SearchView.Regex;

        public ToggleButton FavoritesFileTabButton => FavoritesView.FileTab;
        public ToggleButton FavoritesFolderTabButton => FavoritesView.FolderTab;
        public ToggleButton RecentFileTabButton => RecentView.FileTab;
        public ToggleButton RecentFolderTabButton => RecentView.FolderTab;

        public void SetExplorerTreeMode(bool isTreeMode)
        {
            ExplorerView.SetTreeMode(isTreeMode);
        }

        public void ClearExplorerFilter()
        {
            ExplorerView.ClearFilter();
        }

        public void Localize(Func<string, string, string> getString, bool updateEmptyFolderStatus)
        {
            ToolTipService.SetToolTip(ExplorerActivityButton, getString("Explorer", "탐색기"));
            ToolTipService.SetToolTip(FavoritesActivityButton, getString("Favorites", "즐겨찾기"));
            ToolTipService.SetToolTip(SnippetsActivityButton, getString("Snippets", "스니펫"));
            ToolTipService.SetToolTip(GitActivityButton, getString("Git", "Git"));
            ToolTipService.SetToolTip(SearchActivityButton, getString("Search", "검색"));
            ToolTipService.SetToolTip(RecentActivityButton, getString("RecentFiles", "최근 파일"));
            ToolTipService.SetToolTip(TocActivityButton, getString("TOC", "목차 (TOC)"));
            ExplorerView.Localize(getString, updateEmptyFolderStatus);
            FavoritesView.Localize(getString);
            RecentView.Localize(getString);
            SearchView.Localize(getString);
            GitView.Localize(getString);
            SnippetsView.Localize(getString);
            TocView.Localize(getString);
        }

        public int ShowPage(int index)
        {
            Grid[] pages =
            {
                ExplorerView.Root,
                FavoritesView.Root,
                RecentView.Root,
                SearchView.Root,
                GitView.Root,
                SnippetsView.Root,
                TocView.Root
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
    }
}
