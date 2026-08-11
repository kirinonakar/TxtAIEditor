using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed partial class ExplorerSidebarView : UserControl
    {
        private TreeViewNode? _treeSelectionAnchor;
        private TreeViewNode? _treeKeyboardFocusNode;
        private bool _isApplyingTreeSelection;
        private bool _isTreeSelectionCountUpdateQueued;

        public ExplorerSidebarView()
        {
            InitializeComponent();
            RemoteExplorer.RemoteServerSelected += OnRemoteServerSelected;
            RootGrid.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(OnExplorerPointerPressed),
                true);
            RootGrid.AddHandler(
                UIElement.KeyDownEvent,
                new KeyEventHandler(OnExplorerKeyDown),
                true);
            ExplorerTreeView.SelectionChanged += OnExplorerTreeSelectionChanged;
        }

        public Grid Root => RootGrid;
        public TextBlock Status => ExplorerStatusText;
        public ExplorerPathBar Breadcrumb => ExplorerPathBarControl;
        public Button BackButton => ExplorerBackButton;
        public Button UpButton => ExplorerUpButton;
        public Button SelectFolderButton => ExplorerSelectFolderButton;
        public Button CreateFolderButton => ExplorerCreateFolderButton;
        public Button RefreshButton => ExplorerRefreshButton;
        public Button SortButton => ExplorerSortButton;
        public Button RemoteButton => ExplorerRemoteButton;
        public Button OpenInWindowsButton => ExplorerOpenInWindowsButton;
        public Button HomeButton => ExplorerHomeButton;
        public ToggleButton TreeModeButton => ExplorerTreeModeButton;
        public ToggleButton HideUnwantedButton => ExplorerHideUnwantedButton;
        public TreeView Tree => ExplorerTreeView;
        public ListView FileList => FileListView;

        public event RoutedEventHandler? BackClick;
        public event RoutedEventHandler? ForwardClick;
        public event RoutedEventHandler? UpClick;
        public event RoutedEventHandler? SelectFolderClick;
        public event RoutedEventHandler? CreateFolderClick;
        public event RoutedEventHandler? CreateFileClick;
        public event RoutedEventHandler? CreateNotebookClick;
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
        public event Action<int>? TreeSelectionCountChanged;
        public event DragEventHandler? TreeDragOver;
        public event DragEventHandler? TreeDrop;
        public event ItemClickEventHandler? FileItemClick;
        public event RightTappedEventHandler? FileItemRightTapped;
        public event RoutedEventHandler? CutClick;
        public event RoutedEventHandler? CopyItemsClick;
        public event RoutedEventHandler? PasteClick;
        public event RoutedEventHandler? AddFileToFavoritesClick;
        public event RoutedEventHandler? AddFolderToFavoritesClick;
        public event RoutedEventHandler? InsertMarkdownImageClick;
        public event RoutedEventHandler? OpenExternalViewerClick;
        public event RoutedEventHandler? OpenWithDefaultProgramClick;
        public event RoutedEventHandler? ExtractArchiveToFolderClick;
        public event RoutedEventHandler? CompressFolderToZipClick;
        public event RoutedEventHandler? CompressFolderToSevenZipClick;
        public event RoutedEventHandler? ImageConversionClick;
        public event RoutedEventHandler? DownloadRemoteItemClick;
        public event RoutedEventHandler? UploadRemoteItemClick;
        public event RoutedEventHandler? CopyFileNameClick;
        public event RoutedEventHandler? CopyFilePathClick;
        public event RoutedEventHandler? CopyFolderPathClick;
        public event RoutedEventHandler? RenameClick;
        public event RoutedEventHandler? DeleteClick;
        public event TextChangedEventHandler? FilterTextChanged;
        public event RoutedEventHandler? HideUnwantedChanged;
        public event DragEventHandler? FileListDragOver;
        public event DragEventHandler? FileListDrop;
        public event DragEventHandler? FileItemDragOver;
        public event DragEventHandler? FileItemDrop;

        public void SetTreeMode(bool isTreeMode)
        {
            ExplorerTreeModeButton.IsChecked = isTreeMode;
            FileListView.Visibility = isTreeMode ? Visibility.Collapsed : Visibility.Visible;
            ExplorerTreeView.Visibility = isTreeMode ? Visibility.Visible : Visibility.Collapsed;
            ExplorerBackButton.IsEnabled = !isTreeMode;
            ExplorerUpButton.IsEnabled = !isTreeMode;

            if (isTreeMode)
            {
                ExplorerTreeView.DispatcherQueue.TryEnqueue(() =>
                {
                    if (ExplorerTreeView.Visibility == Visibility.Visible)
                    {
                        ExplorerTreeView.Focus(FocusState.Programmatic);
                    }
                });
            }
        }

        public void ClearTreeSelection()
        {
            _treeSelectionAnchor = null;
            _treeKeyboardFocusNode = null;
            _isApplyingTreeSelection = true;
            try
            {
                ExplorerTreeView.SelectedNodes.Clear();
            }
            finally
            {
                _isApplyingTreeSelection = false;
            }

            NotifyTreeSelectionCountChanged();
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

            string backText = getString("ExplorerBackTooltip", "이전 폴더");
            ToolTipService.SetToolTip(ExplorerBackButton, backText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerBackButton, backText);
            string upText = getString("ExplorerUpTooltip", "상위 폴더");
            ToolTipService.SetToolTip(ExplorerUpButton, upText);
            var selectFolderText = getString("ExplorerSelectFolder", "폴더 선택...");
            ToolTipService.SetToolTip(ExplorerSelectFolderButton, selectFolderText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerSelectFolderButton, selectFolderText);
            string createItemText = getString("ExplorerCreateItemTooltip", "새 항목");
            ToolTipService.SetToolTip(ExplorerCreateFolderButton, createItemText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerCreateFolderButton, createItemText);
            ExplorerCreateFolderMenuItem.Text = getString("ExplorerCreateFolderTooltip", "새 폴더");
            ExplorerCreateFileMenuItem.Text = getString("ExplorerCreateFileTooltip", "새 파일");
            ExplorerCreateNotebookMenuItem.Text = getString("ExplorerCreateNotebookTooltip", "새 노트북");
            string refreshText = getString("ExplorerRefreshTooltip", "새로고침");
            ToolTipService.SetToolTip(ExplorerRefreshButton, refreshText);
            string sortText = getString("ExplorerSortName", "이름순 정렬");
            ToolTipService.SetToolTip(ExplorerSortButton, sortText);

            var remoteExplorerText = getString("RemoteExplorerTitle", "리모트 서버");
            ToolTipService.SetToolTip(ExplorerRemoteButton, remoteExplorerText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerRemoteButton, remoteExplorerText);
            RemoteExplorer.Localize(getString);

            var openInWindowsExplorerText = getString("ExplorerOpenInWindowsTooltip", "Windows 탐색기에서 열기");
            ToolTipService.SetToolTip(ExplorerOpenInWindowsButton, openInWindowsExplorerText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerOpenInWindowsButton, openInWindowsExplorerText);

            ExplorerFilterBox.PlaceholderText = getString("ExplorerFilterPlaceholder", "파일명 필터 (하위 폴더 포함)...");

            var hideUnwantedText = getString("ExplorerHideUnwantedTooltip", ".venv 등 .으로 시작하는 폴더와 node_modules, obj 보이기");
            ToolTipService.SetToolTip(ExplorerHideUnwantedButton, hideUnwantedText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerHideUnwantedButton, hideUnwantedText);

            var homeFolderText = getString("ExplorerHomeFolderTooltip", "홈 폴더로 이동");
            ToolTipService.SetToolTip(ExplorerHomeButton, homeFolderText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerHomeButton, homeFolderText);

            var treeModeText = getString("ExplorerTreeModeTooltip", "트리 모드 (F3)");
            ToolTipService.SetToolTip(ExplorerTreeModeButton, treeModeText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerTreeModeButton, treeModeText);

            string moreActionsText = getString("ExplorerMoreActions", "더 보기");
            ToolTipService.SetToolTip(ExplorerNavigationOverflowButton, moreActionsText);
            ToolTipService.SetToolTip(ExplorerStatusOverflowButton, moreActionsText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerNavigationOverflowButton, moreActionsText);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerStatusOverflowButton, moreActionsText);

            ExplorerOverflowRefreshMenuItem.Text = refreshText;
            ExplorerOverflowHomeMenuItem.Text = homeFolderText;
            ExplorerOverflowSelectFolderMenuItem.Text = selectFolderText;
            ExplorerOverflowCreateSubItem.Text = createItemText;
            ExplorerOverflowCreateFolderMenuItem.Text = ExplorerCreateFolderMenuItem.Text;
            ExplorerOverflowCreateFileMenuItem.Text = ExplorerCreateFileMenuItem.Text;
            ExplorerOverflowCreateNotebookMenuItem.Text = ExplorerCreateNotebookMenuItem.Text;
            ExplorerOverflowRemoteMenuItem.Text = remoteExplorerText;
            ExplorerOverflowOpenInWindowsMenuItem.Text = openInWindowsExplorerText;
        }

        private void OnExplorerNavigationToolbarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool showAll = e.NewSize.Width >= 205;
            bool showNavigationActions = e.NewSize.Width >= 177;

            ExplorerRefreshButton.Visibility = showAll || showNavigationActions ? Visibility.Visible : Visibility.Collapsed;
            ExplorerHomeButton.Visibility = showAll || showNavigationActions ? Visibility.Visible : Visibility.Collapsed;
            ExplorerSelectFolderButton.Visibility = showAll ? Visibility.Visible : Visibility.Collapsed;
            ExplorerCreateFolderButton.Visibility = showAll ? Visibility.Visible : Visibility.Collapsed;
            ExplorerNavigationOverflowButton.Visibility = showAll ? Visibility.Collapsed : Visibility.Visible;

            ExplorerOverflowRefreshMenuItem.Visibility = showNavigationActions ? Visibility.Collapsed : Visibility.Visible;
            ExplorerOverflowHomeMenuItem.Visibility = showNavigationActions ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnExplorerStatusToolbarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool showAll = e.NewSize.Width >= 190;
            ExplorerRemoteButton.Visibility = showAll ? Visibility.Visible : Visibility.Collapsed;
            ExplorerOpenInWindowsButton.Visibility = showAll ? Visibility.Visible : Visibility.Collapsed;
            ExplorerStatusOverflowButton.Visibility = showAll ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnExplorerBackClick(object sender, RoutedEventArgs e) => BackClick?.Invoke(sender, e);
        private void OnExplorerPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(RootGrid).Properties;
            if (properties.IsXButton1Pressed)
            {
                e.Handled = true;
                BackClick?.Invoke(this, new RoutedEventArgs());
            }
            else if (properties.IsXButton2Pressed)
            {
                e.Handled = true;
                ForwardClick?.Invoke(this, new RoutedEventArgs());
                return;
            }

            if (ExplorerTreeView.Visibility != Visibility.Visible ||
                e.OriginalSource is not DependencyObject source ||
                !IsDescendantOf(source, ExplorerTreeView))
            {
                return;
            }

            // The built-in chevron handles PointerPressed itself. When the tree has
            // not received focus yet, some input paths can leave that event unhandled.
            // Focus the tree for both paths, and only provide a fallback toggle when
            // WinUI did not handle the chevron press.
            ExplorerTreeView.Focus(FocusState.Pointer);
            if (TryGetTreeExpanderNode(source, out TreeViewNode? expanderNode) && expanderNode != null)
            {
                if (e.Handled)
                {
                    return;
                }

                ExplorerTreeView.DispatcherQueue.TryEnqueue(() =>
                {
                    if (ExplorerTreeView.Visibility == Visibility.Visible &&
                        IndexOfNode(GetVisibleTreeNodes(), expanderNode) >= 0)
                    {
                        expanderNode.IsExpanded = !expanderNode.IsExpanded;
                    }
                });
                return;
            }

            if (!TryGetTreeNode(source, out TreeViewNode? node) || node == null || !properties.IsLeftButtonPressed)
            {
                return;
            }

            _treeKeyboardFocusNode = node;
            bool controlPressed = IsTreeModifierPressed(Windows.System.VirtualKey.Control);
            bool shiftPressed = IsTreeModifierPressed(Windows.System.VirtualKey.Shift);
            if (!controlPressed && !shiftPressed)
            {
                _treeSelectionAnchor = node;
                return;
            }

            TreeViewNode selectionAnchor = GetSelectionAnchor(node);
            bool wasSelected = IsTreeNodeSelected(node);
            ExplorerTreeView.DispatcherQueue.TryEnqueue(() =>
            {
                ApplyPointerSelection(node, selectionAnchor, controlPressed, shiftPressed, wasSelected);
            });
            e.Handled = true;
        }

        private void OnExplorerKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (ExplorerTreeView.Visibility != Visibility.Visible ||
                !IsTreeModifierPressed(Windows.System.VirtualKey.Shift) ||
                IsTreeModifierPressed(Windows.System.VirtualKey.Control) ||
                (e.Key != Windows.System.VirtualKey.Up && e.Key != Windows.System.VirtualKey.Down))
            {
                return;
            }

            TreeViewNode? currentNode = null;
            if (e.OriginalSource is DependencyObject source)
            {
                TryGetTreeNode(source, out currentNode);
            }

            currentNode ??= _treeKeyboardFocusNode;
            if (currentNode == null)
            {
                return;
            }

            List<TreeViewNode> visibleNodes = GetVisibleTreeNodes();
            int currentIndex = IndexOfNode(visibleNodes, currentNode);
            if (currentIndex < 0)
            {
                return;
            }

            int nextIndex = e.Key == Windows.System.VirtualKey.Up
                ? currentIndex - 1
                : currentIndex + 1;
            if (nextIndex < 0 || nextIndex >= visibleNodes.Count)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            int direction = e.Key == Windows.System.VirtualKey.Up ? -1 : 1;
            ExplorerTreeView.DispatcherQueue.TryEnqueue(() =>
            {
                ApplyShiftArrowSelection(currentNode, direction);
            });
        }

        private void OnExplorerTreeSelectionChanged(
            TreeView sender,
            TreeViewSelectionChangedEventArgs args)
        {
            if (_isApplyingTreeSelection)
            {
                return;
            }

            QueueTreeSelectionCountChanged();

            // Do not inspect SelectedNodes here. WinUI can raise this event while
            // it is detaching selected nodes from RootNodes, and re-entering the
            // selection projection at that point can fail inside Microsoft.UI.Xaml.
            if (args.AddedItems.Count == 0)
            {
                _treeSelectionAnchor = null;
                _treeKeyboardFocusNode = null;
                return;
            }

            object selectedItem = args.AddedItems[args.AddedItems.Count - 1];
            if (TryGetInvokedTreeNode(selectedItem, out TreeViewNode? selectedNode) && selectedNode != null)
            {
                _treeSelectionAnchor = selectedNode;
                _treeKeyboardFocusNode = selectedNode;
            }
        }

        private void ApplyPointerSelection(
            TreeViewNode node,
            TreeViewNode selectionAnchor,
            bool controlPressed,
            bool shiftPressed,
            bool wasSelected)
        {
            if (ExplorerTreeView.Visibility != Visibility.Visible ||
                IndexOfNode(GetVisibleTreeNodes(), node) < 0)
            {
                return;
            }

            if (shiftPressed)
            {
                ApplyTreeRangeSelection(selectionAnchor, node, preserveExistingSelection: controlPressed);
            }
            else
            {
                SetTreeNodeSelection(node, !wasSelected);
                _treeSelectionAnchor = node;
            }

            FocusTreeNode(node);
            _treeKeyboardFocusNode = node;
        }

        private void ApplyShiftArrowSelection(TreeViewNode currentNode, int direction)
        {
            if (ExplorerTreeView.Visibility != Visibility.Visible)
            {
                return;
            }

            List<TreeViewNode> visibleNodes = GetVisibleTreeNodes();
            int currentIndex = IndexOfNode(visibleNodes, currentNode);
            if (currentIndex < 0)
            {
                return;
            }

            int targetIndex = currentIndex + direction;
            if (targetIndex < 0 || targetIndex >= visibleNodes.Count)
            {
                return;
            }

            TreeViewNode targetNode = visibleNodes[targetIndex];
            TreeViewNode selectionAnchor = GetSelectionAnchor(currentNode);
            ApplyTreeRangeSelection(selectionAnchor, targetNode, preserveExistingSelection: false);
            FocusTreeNode(targetNode);
            _treeKeyboardFocusNode = targetNode;
        }

        private void SelectTreeNodeAsCurrent(TreeViewNode node)
        {
            if (ExplorerTreeView.Visibility != Visibility.Visible ||
                IndexOfNode(GetVisibleTreeNodes(), node) < 0 ||
                !IsTreeRangeSelectableNode(node))
            {
                return;
            }

            _isApplyingTreeSelection = true;
            try
            {
                ExplorerTreeView.SelectedNodes.Clear();
                ExplorerTreeView.SelectedNodes.Add(node);
            }
            finally
            {
                _isApplyingTreeSelection = false;
            }

            _treeSelectionAnchor = node;
            _treeKeyboardFocusNode = node;
            NotifyTreeSelectionCountChanged();
        }

        private void ApplyTreeRangeSelection(
            TreeViewNode selectionAnchor,
            TreeViewNode targetNode,
            bool preserveExistingSelection)
        {
            List<TreeViewNode> visibleNodes = GetVisibleTreeNodes();
            int anchorIndex = IndexOfNode(visibleNodes, selectionAnchor);
            int targetIndex = IndexOfNode(visibleNodes, targetNode);
            if (targetIndex < 0)
            {
                return;
            }

            if (anchorIndex < 0)
            {
                anchorIndex = targetIndex;
            }

            int rangeStart = Math.Min(anchorIndex, targetIndex);
            int rangeEnd = Math.Max(anchorIndex, targetIndex);
            var rangeNodes = new List<TreeViewNode>();
            for (int index = rangeStart; index <= rangeEnd; index++)
            {
                TreeViewNode node = visibleNodes[index];
                if (IsTreeRangeSelectableNode(node))
                {
                    rangeNodes.Add(node);
                }
            }

            if (rangeNodes.Count == 0)
            {
                return;
            }

            _isApplyingTreeSelection = true;
            try
            {
                if (!preserveExistingSelection)
                {
                    ExplorerTreeView.SelectedNodes.Clear();
                }

                foreach (TreeViewNode node in rangeNodes)
                {
                    if (!IsTreeNodeSelected(node))
                    {
                        ExplorerTreeView.SelectedNodes.Add(node);
                    }
                }
            }
            finally
            {
                _isApplyingTreeSelection = false;
            }

            _treeSelectionAnchor = selectionAnchor;
            NotifyTreeSelectionCountChanged();
        }

        private static bool IsTreeRangeSelectableNode(TreeViewNode node)
        {
            return node.Content is ExplorerItem item &&
                   !item.IsFolder &&
                   !node.HasUnrealizedChildren &&
                   node.Children.Count == 0;
        }

        private void SetTreeNodeSelection(TreeViewNode node, bool isSelected)
        {
            _isApplyingTreeSelection = true;
            try
            {
                bool currentlySelected = IsTreeNodeSelected(node);
                if (currentlySelected == isSelected)
                {
                    return;
                }

                if (isSelected)
                {
                    ExplorerTreeView.SelectedNodes.Add(node);
                }
                else
                {
                    int selectedIndex = IndexOfSelectedNode(node);
                    if (selectedIndex >= 0)
                    {
                        ExplorerTreeView.SelectedNodes.RemoveAt(selectedIndex);
                    }
                }
            }
            finally
            {
                _isApplyingTreeSelection = false;
            }

            NotifyTreeSelectionCountChanged();
        }

        private void QueueTreeSelectionCountChanged()
        {
            if (_isTreeSelectionCountUpdateQueued)
            {
                return;
            }

            _isTreeSelectionCountUpdateQueued = true;
            bool enqueued = ExplorerTreeView.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    _isTreeSelectionCountUpdateQueued = false;
                    NotifyTreeSelectionCountChanged();
                });

            if (!enqueued)
            {
                _isTreeSelectionCountUpdateQueued = false;
            }
        }

        private void NotifyTreeSelectionCountChanged()
        {
            int selectedCount = 0;
            foreach (TreeViewNode node in ExplorerTreeView.SelectedNodes)
            {
                if (IsTreeRangeSelectableNode(node))
                {
                    selectedCount++;
                }
            }

            TreeSelectionCountChanged?.Invoke(selectedCount);
        }

        private TreeViewNode GetSelectionAnchor(TreeViewNode fallback)
        {
            List<TreeViewNode> visibleNodes = GetVisibleTreeNodes();
            return _treeSelectionAnchor != null && IndexOfNode(visibleNodes, _treeSelectionAnchor) >= 0
                ? _treeSelectionAnchor
                : fallback;
        }

        private bool IsTreeNodeSelected(TreeViewNode node)
        {
            return IndexOfSelectedNode(node) >= 0;
        }

        private int IndexOfSelectedNode(TreeViewNode node)
        {
            for (int index = 0; index < ExplorerTreeView.SelectedNodes.Count; index++)
            {
                if (AreSameTreeNode(ExplorerTreeView.SelectedNodes[index], node))
                {
                    return index;
                }
            }

            return -1;
        }

        private static int IndexOfNode(IReadOnlyList<TreeViewNode> nodes, TreeViewNode node)
        {
            for (int index = 0; index < nodes.Count; index++)
            {
                if (AreSameTreeNode(nodes[index], node))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool AreSameTreeNode(TreeViewNode left, TreeViewNode right)
        {
            return ReferenceEquals(left, right) ||
                   Equals(left, right) ||
                   (left.Content is ExplorerItem leftItem &&
                    right.Content is ExplorerItem rightItem &&
                    ReferenceEquals(leftItem, rightItem));
        }

        private List<TreeViewNode> GetVisibleTreeNodes()
        {
            var nodes = new List<TreeViewNode>();
            foreach (TreeViewNode rootNode in ExplorerTreeView.RootNodes)
            {
                AddVisibleTreeNode(rootNode, nodes);
            }

            return nodes;
        }

        private static void AddVisibleTreeNode(TreeViewNode node, List<TreeViewNode> nodes)
        {
            nodes.Add(node);
            if (!node.IsExpanded)
            {
                return;
            }

            foreach (TreeViewNode childNode in node.Children)
            {
                AddVisibleTreeNode(childNode, nodes);
            }
        }

        private void FocusTreeNode(TreeViewNode node)
        {
            if (ExplorerTreeView.ContainerFromNode(node) is TreeViewItem treeViewItem)
            {
                treeViewItem.Focus(FocusState.Keyboard);
            }
            else
            {
                ExplorerTreeView.Focus(FocusState.Keyboard);
            }
        }

        private bool TryGetTreeNode(DependencyObject source, out TreeViewNode? node)
        {
            node = null;
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is TreeViewItem treeViewItem)
                {
                    node = ExplorerTreeView.NodeFromContainer(treeViewItem);
                    return node != null;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private static bool IsTreeModifierPressed(Windows.System.VirtualKey key)
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        private bool TryGetTreeExpanderNode(DependencyObject source, out TreeViewNode? node)
        {
            node = null;
            TreeViewItem? container = null;
            bool isExpander = false;
            DependencyObject? current = source;

            while (current != null)
            {
                if (current is FrameworkElement element &&
                    string.Equals(element.Name, "ExpandCollapseChevron", StringComparison.Ordinal))
                {
                    isExpander = true;
                }

                if (current is TreeViewItem treeViewItem)
                {
                    container = treeViewItem;
                    break;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            if (!isExpander || container == null)
            {
                return false;
            }

            node = ExplorerTreeView.NodeFromContainer(container);
            return node != null;
        }

        private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }
        private void OnExplorerUpClick(object sender, RoutedEventArgs e) => UpClick?.Invoke(sender, e);
        private void OnSelectFolderClick(object sender, RoutedEventArgs e) => SelectFolderClick?.Invoke(sender, e);
        private void OnCreateFolderClick(object sender, RoutedEventArgs e) => CreateFolderClick?.Invoke(sender, e);
        private void OnCreateFileClick(object sender, RoutedEventArgs e) => CreateFileClick?.Invoke(sender, e);
        private void OnCreateNotebookClick(object sender, RoutedEventArgs e) => CreateNotebookClick?.Invoke(sender, e);
        private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshClick?.Invoke(sender, e);
        private void OnSortClick(object sender, RoutedEventArgs e) => SortClick?.Invoke(sender, e);
        private async void OnRemoteFlyoutOpening(object sender, object e) => await RemoteExplorer.RefreshProfilesAsync();
        private void OnOverflowRemoteClick(object sender, RoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => ExplorerRemoteButton.Flyout?.ShowAt(ExplorerStatusOverflowButton));
        }
        private void OnOpenInWindowsExplorerClick(object sender, RoutedEventArgs e) => OpenInWindowsExplorerClick?.Invoke(sender, e);
        private void OnExplorerHomeClick(object sender, RoutedEventArgs e) => HomeClick?.Invoke(sender, e);
        private void OnExplorerTreeModeClick(object sender, RoutedEventArgs e) => TreeModeClick?.Invoke(sender, e);
        private void OnExplorerTreeExpanding(TreeView sender, TreeViewExpandingEventArgs e) => TreeExpanding?.Invoke(sender, e);
        private void OnExplorerTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs e)
        {
            if (!IsTreeModifierPressed(Windows.System.VirtualKey.Control) &&
                !IsTreeModifierPressed(Windows.System.VirtualKey.Shift) &&
                TryGetInvokedTreeNode(e.InvokedItem, out TreeViewNode? node) &&
                node != null &&
                IsTreeRangeSelectableNode(node))
            {
                _treeSelectionAnchor = node;
                _treeKeyboardFocusNode = node;
                ExplorerTreeView.DispatcherQueue.TryEnqueue(() => SelectTreeNodeAsCurrent(node));
            }

            TreeItemInvoked?.Invoke(sender, e);
        }

        private bool TryGetInvokedTreeNode(object? invokedItem, out TreeViewNode? node)
        {
            node = invokedItem as TreeViewNode;
            if (node != null)
            {
                return true;
            }

            if (invokedItem is not ExplorerItem item)
            {
                return false;
            }

            foreach (TreeViewNode rootNode in ExplorerTreeView.RootNodes)
            {
                node = FindTreeNodeByContent(rootNode, item);
                if (node != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static TreeViewNode? FindTreeNodeByContent(TreeViewNode node, ExplorerItem item)
        {
            if (ReferenceEquals(node.Content, item))
            {
                return node;
            }

            foreach (TreeViewNode childNode in node.Children)
            {
                TreeViewNode? match = FindTreeNodeByContent(childNode, item);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
        private void OnExplorerTreeDragOver(object sender, DragEventArgs e) => TreeDragOver?.Invoke(sender, e);
        private void OnExplorerTreeDrop(object sender, DragEventArgs e) => TreeDrop?.Invoke(sender, e);
        private void OnFileListViewItemClick(object sender, ItemClickEventArgs e) => FileItemClick?.Invoke(sender, e);
        private void OnFileListViewItemRightTapped(object sender, RightTappedRoutedEventArgs e) => FileItemRightTapped?.Invoke(sender, e);
        private void OnCutClick(object sender, RoutedEventArgs e) => CutClick?.Invoke(sender, e);
        private void OnCopyItemsClick(object sender, RoutedEventArgs e) => CopyItemsClick?.Invoke(sender, e);
        private void OnPasteClick(object sender, RoutedEventArgs e) => PasteClick?.Invoke(sender, e);
        private void OnAddFileToFavoritesClick(object sender, RoutedEventArgs e) => AddFileToFavoritesClick?.Invoke(sender, e);
        private void OnAddFolderToFavoritesClick(object sender, RoutedEventArgs e) => AddFolderToFavoritesClick?.Invoke(sender, e);
        private void OnInsertMarkdownImageClick(object sender, RoutedEventArgs e) => InsertMarkdownImageClick?.Invoke(sender, e);
        private void OnOpenExternalViewerClick(object sender, RoutedEventArgs e) => OpenExternalViewerClick?.Invoke(sender, e);
        private void OnOpenWithDefaultProgramClick(object sender, RoutedEventArgs e) => OpenWithDefaultProgramClick?.Invoke(sender, e);
        private void OnExtractArchiveToFolderClick(object sender, RoutedEventArgs e) => ExtractArchiveToFolderClick?.Invoke(sender, e);
        private void OnCompressFolderToZipClick(object sender, RoutedEventArgs e) => CompressFolderToZipClick?.Invoke(sender, e);
        private void OnCompressFolderToSevenZipClick(object sender, RoutedEventArgs e) => CompressFolderToSevenZipClick?.Invoke(sender, e);
        private void OnImageConversionClick(object sender, RoutedEventArgs e) => ImageConversionClick?.Invoke(sender, e);
        private void OnDownloadRemoteItemClick(object sender, RoutedEventArgs e) => DownloadRemoteItemClick?.Invoke(sender, e);
        private void OnUploadRemoteItemClick(object sender, RoutedEventArgs e) => UploadRemoteItemClick?.Invoke(sender, e);
        private void OnCopyFileNameClick(object sender, RoutedEventArgs e) => CopyFileNameClick?.Invoke(sender, e);
        private void OnCopyFilePathClick(object sender, RoutedEventArgs e) => CopyFilePathClick?.Invoke(sender, e);
        private void OnCopyFolderPathClick(object sender, RoutedEventArgs e) => CopyFolderPathClick?.Invoke(sender, e);
        private void OnRenameClick(object sender, RoutedEventArgs e) => RenameClick?.Invoke(sender, e);
        private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteClick?.Invoke(sender, e);
        private void OnExplorerFilterTextChanged(object sender, TextChangedEventArgs e) => FilterTextChanged?.Invoke(sender, e);
        private void OnHideUnwantedClick(object sender, RoutedEventArgs e) => HideUnwantedChanged?.Invoke(sender, e);
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
