using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed partial class ExplorerSidebarView : UserControl
    {
        public ExplorerSidebarView()
        {
            InitializeComponent();
            RemoteExplorer.RemoteServerSelected += OnRemoteServerSelected;
        }

        public Grid Root => RootGrid;
        public TextBlock Status => ExplorerStatusText;
        public Button UpButton => ExplorerUpButton;
        public Button SelectFolderButton => ExplorerSelectFolderButton;
        public Button CreateFolderButton => ExplorerCreateFolderButton;
        public Button RefreshButton => ExplorerRefreshButton;
        public Button SortButton => ExplorerSortButton;
        public Button RemoteButton => ExplorerRemoteButton;
        public Button OpenInWindowsButton => ExplorerOpenInWindowsButton;
        public Button HomeButton => ExplorerHomeButton;
        public ToggleButton TreeModeButton => ExplorerTreeModeButton;
        public TreeView Tree => ExplorerTreeView;
        public ListView FileList => FileListView;

        public event RoutedEventHandler? UpClick;
        public event RoutedEventHandler? SelectFolderClick;
        public event RoutedEventHandler? CreateFolderClick;
        public event RoutedEventHandler? CreateFileClick;
        public event RoutedEventHandler? RefreshClick;
        public event RoutedEventHandler? SortClick;
        public event EventHandler<RemoteFileOpenedEventArgs>? RemoteFileOpened
        {
            add => RemoteExplorer.RemoteFileOpened += value;
            remove => RemoteExplorer.RemoteFileOpened -= value;
        }
        public event EventHandler<RemoteServerSelectedEventArgs>? RemoteServerSelected;
        public event RoutedEventHandler? OpenInWindowsExplorerClick;
        public event RoutedEventHandler? HomeClick;
        public event RoutedEventHandler? TreeModeClick;
        public event EventHandler<TreeViewExpandingEventArgs>? TreeExpanding;
        public event EventHandler<TreeViewItemInvokedEventArgs>? TreeItemInvoked;
        public event DragEventHandler? TreeDragOver;
        public event DragEventHandler? TreeDrop;
        public event ItemClickEventHandler? FileItemClick;
        public event RightTappedEventHandler? FileItemRightTapped;
        public event RoutedEventHandler? AddFileToFavoritesClick;
        public event RoutedEventHandler? AddFolderToFavoritesClick;
        public event RoutedEventHandler? InsertMarkdownImageClick;
        public event RoutedEventHandler? OpenExternalViewerClick;
        public event RoutedEventHandler? OpenWithDefaultProgramClick;
        public event RoutedEventHandler? ExtractArchiveToFolderClick;
        public event RoutedEventHandler? CompressFolderToZipClick;
        public event RoutedEventHandler? CompressFolderToSevenZipClick;
        public event RoutedEventHandler? DownloadRemoteItemClick;
        public event RoutedEventHandler? CopyFileNameClick;
        public event RoutedEventHandler? CopyFilePathClick;
        public event RoutedEventHandler? CopyFolderPathClick;
        public event RoutedEventHandler? RenameClick;
        public event RoutedEventHandler? DeleteClick;
        public event TextChangedEventHandler? FilterTextChanged;
        public event DragEventHandler? FileListDragOver;
        public event DragEventHandler? FileListDrop;
        public event DragEventHandler? FileItemDragOver;
        public event DragEventHandler? FileItemDrop;

        public void SetTreeMode(bool isTreeMode)
        {
            ExplorerTreeModeButton.IsChecked = isTreeMode;
            FileListView.Visibility = isTreeMode ? Visibility.Collapsed : Visibility.Visible;
            ExplorerTreeView.Visibility = isTreeMode ? Visibility.Visible : Visibility.Collapsed;
            ExplorerFilterPanel.Visibility = isTreeMode ? Visibility.Collapsed : Visibility.Visible;
            ExplorerUpButton.IsEnabled = !isTreeMode;
        }

        public void ClearFilter()
        {
            ExplorerFilterBox.Text = string.Empty;
        }

        public void Localize(Func<string, string, string> getString, bool updateEmptyFolderStatus)
        {
            if (updateEmptyFolderStatus)
            {
                ExplorerStatusText.Text = getString("NoFolderSelected", "폴더를 선택하세요.");
            }

            ToolTipService.SetToolTip(ExplorerUpButton, getString("ExplorerUpTooltip", "상위 폴더"));
            var selectFolderText = getString("ExplorerSelectFolder", "폴더 선택...");
            ToolTipService.SetToolTip(ExplorerSelectFolderButton, selectFolderText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerSelectFolderButton, selectFolderText);
            string createItemText = getString("ExplorerCreateItemTooltip", "새 항목");
            ToolTipService.SetToolTip(ExplorerCreateFolderButton, createItemText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerCreateFolderButton, createItemText);
            ExplorerCreateFolderMenuItem.Text = getString("ExplorerCreateFolderTooltip", "새 폴더");
            ExplorerCreateFileMenuItem.Text = getString("ExplorerCreateFileTooltip", "새 파일");
            ToolTipService.SetToolTip(ExplorerRefreshButton, getString("ExplorerRefreshTooltip", "새로고침"));
            ToolTipService.SetToolTip(ExplorerSortButton, getString("ExplorerSortName", "이름순 정렬"));

            var remoteExplorerText = getString("RemoteExplorerTitle", "리모트 서버");
            ToolTipService.SetToolTip(ExplorerRemoteButton, remoteExplorerText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerRemoteButton, remoteExplorerText);
            RemoteExplorer.Localize(getString);

            var openInWindowsExplorerText = getString("ExplorerOpenInWindowsTooltip", "Windows 탐색기에서 열기");
            ToolTipService.SetToolTip(ExplorerOpenInWindowsButton, openInWindowsExplorerText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerOpenInWindowsButton, openInWindowsExplorerText);

            ExplorerFilterBox.PlaceholderText = getString("ExplorerFilterPlaceholder", "파일명 필터 (하위 폴더 포함)...");

            var homeFolderText = getString("ExplorerHomeFolderTooltip", "홈 폴더로 이동");
            ToolTipService.SetToolTip(ExplorerHomeButton, homeFolderText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerHomeButton, homeFolderText);

            var treeModeText = getString("ExplorerTreeModeTooltip", "트리 모드");
            ToolTipService.SetToolTip(ExplorerTreeModeButton, treeModeText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerTreeModeButton, treeModeText);
        }

        private void OnExplorerUpClick(object sender, RoutedEventArgs e) => UpClick?.Invoke(sender, e);
        private void OnSelectFolderClick(object sender, RoutedEventArgs e) => SelectFolderClick?.Invoke(sender, e);
        private void OnCreateFolderClick(object sender, RoutedEventArgs e) => CreateFolderClick?.Invoke(sender, e);
        private void OnCreateFileClick(object sender, RoutedEventArgs e) => CreateFileClick?.Invoke(sender, e);
        private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshClick?.Invoke(sender, e);
        private void OnSortClick(object sender, RoutedEventArgs e) => SortClick?.Invoke(sender, e);
        private async void OnRemoteFlyoutOpening(object sender, object e) => await RemoteExplorer.RefreshProfilesAsync();
        private void OnOpenInWindowsExplorerClick(object sender, RoutedEventArgs e) => OpenInWindowsExplorerClick?.Invoke(sender, e);
        private void OnExplorerHomeClick(object sender, RoutedEventArgs e) => HomeClick?.Invoke(sender, e);
        private void OnExplorerTreeModeClick(object sender, RoutedEventArgs e) => TreeModeClick?.Invoke(sender, e);
        private void OnExplorerTreeExpanding(TreeView sender, TreeViewExpandingEventArgs e) => TreeExpanding?.Invoke(sender, e);
        private void OnExplorerTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs e) => TreeItemInvoked?.Invoke(sender, e);
        private void OnExplorerTreeDragOver(object sender, DragEventArgs e) => TreeDragOver?.Invoke(sender, e);
        private void OnExplorerTreeDrop(object sender, DragEventArgs e) => TreeDrop?.Invoke(sender, e);
        private void OnFileListViewItemClick(object sender, ItemClickEventArgs e) => FileItemClick?.Invoke(sender, e);
        private void OnFileListViewItemRightTapped(object sender, RightTappedRoutedEventArgs e) => FileItemRightTapped?.Invoke(sender, e);
        private void OnAddFileToFavoritesClick(object sender, RoutedEventArgs e) => AddFileToFavoritesClick?.Invoke(sender, e);
        private void OnAddFolderToFavoritesClick(object sender, RoutedEventArgs e) => AddFolderToFavoritesClick?.Invoke(sender, e);
        private void OnInsertMarkdownImageClick(object sender, RoutedEventArgs e) => InsertMarkdownImageClick?.Invoke(sender, e);
        private void OnOpenExternalViewerClick(object sender, RoutedEventArgs e) => OpenExternalViewerClick?.Invoke(sender, e);
        private void OnOpenWithDefaultProgramClick(object sender, RoutedEventArgs e) => OpenWithDefaultProgramClick?.Invoke(sender, e);
        private void OnExtractArchiveToFolderClick(object sender, RoutedEventArgs e) => ExtractArchiveToFolderClick?.Invoke(sender, e);
        private void OnCompressFolderToZipClick(object sender, RoutedEventArgs e) => CompressFolderToZipClick?.Invoke(sender, e);
        private void OnCompressFolderToSevenZipClick(object sender, RoutedEventArgs e) => CompressFolderToSevenZipClick?.Invoke(sender, e);
        private void OnDownloadRemoteItemClick(object sender, RoutedEventArgs e) => DownloadRemoteItemClick?.Invoke(sender, e);
        private void OnCopyFileNameClick(object sender, RoutedEventArgs e) => CopyFileNameClick?.Invoke(sender, e);
        private void OnCopyFilePathClick(object sender, RoutedEventArgs e) => CopyFilePathClick?.Invoke(sender, e);
        private void OnCopyFolderPathClick(object sender, RoutedEventArgs e) => CopyFolderPathClick?.Invoke(sender, e);
        private void OnRenameClick(object sender, RoutedEventArgs e) => RenameClick?.Invoke(sender, e);
        private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteClick?.Invoke(sender, e);
        private void OnExplorerFilterTextChanged(object sender, TextChangedEventArgs e) => FilterTextChanged?.Invoke(sender, e);
        private void OnFileListViewDragOver(object sender, DragEventArgs e) => FileListDragOver?.Invoke(sender, e);
        private void OnFileListViewDrop(object sender, DragEventArgs e) => FileListDrop?.Invoke(sender, e);
        private void OnFileListViewItemDragOver(object sender, DragEventArgs e) => FileItemDragOver?.Invoke(sender, e);
        private void OnFileListViewItemDrop(object sender, DragEventArgs e) => FileItemDrop?.Invoke(sender, e);

        private void OnRemoteServerSelected(object? sender, RemoteServerSelectedEventArgs e)
        {
            ExplorerRemoteButton.Flyout?.Hide();
            RemoteServerSelected?.Invoke(this, e);
        }
    }
}
