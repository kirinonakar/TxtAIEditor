using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace TxtAIEditor.Controls
{
    public enum EditorSplitMode
    {
        None,
        Vertical,
        Horizontal
    }

    public sealed partial class EditorWorkspacePane : UserControl
    {
        private const double TerminalSplitterVisibleThickness = 2;

        private readonly TerminalPane _terminalPane = new TerminalPane();
        private bool _isDraggingEditorSplitter = false;
        private double _editorSplitterStartWidth = 0;
        private double _editorSplitterStartHeight = 0;
        private double _editorSplitterStartPointerX = 0;
        private double _editorSplitterStartPointerY = 0;
        private bool _isVerticalSplit = true;

        private bool _isDraggingTerminalSplitter = false;
        private double _terminalSplitterStartHeight = 0;
        private double _terminalSplitterStartPointerY = 0;

        public EditorWorkspacePane()
        {
            InitializeComponent();
            TerminalPaneHost.Content = _terminalPane;
            _terminalPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            _terminalPane.VerticalAlignment = VerticalAlignment.Stretch;
            _terminalPane.Visibility = Visibility.Collapsed;
            SizeChanged += OnWorkspaceSizeChanged;
            ActiveTabView = EditorTabView;
            Loaded += (_, __) => DisableTabItemTransitions();
        }

        public event TypedEventHandler<TabView, object>? PrimaryAddTabButtonClick;
        public event TypedEventHandler<TabView, TabViewTabCloseRequestedEventArgs>? PrimaryTabCloseRequested;
        public event SelectionChangedEventHandler? PrimarySelectionChanged;
        public event TypedEventHandler<TabView, object>? SecondaryAddTabButtonClick;
        public event TypedEventHandler<TabView, TabViewTabCloseRequestedEventArgs>? SecondaryTabCloseRequested;
        public event SelectionChangedEventHandler? SecondarySelectionChanged;
        public event RoutedEventHandler? TabViewGotFocus;
        public event RoutedEventHandler? MoveTabLeftClick;
        public event RoutedEventHandler? MoveTabRightClick;
        public event EventHandler? TerminalPanelHeightChanged;

        public TabView? ActiveTabView { get; set; }
        public EditorSplitMode CurrentSplitMode { get; private set; } = EditorSplitMode.None;
        public double LastTerminalHeight { get; set; } = 220;

        public TabView EditorTabViewControl => EditorTabView;
        public TabView EditorTabView2Control => EditorTabView2;
        public TerminalPane TerminalPaneControl => _terminalPane;
        private TerminalPane TerminalPane => _terminalPane;

        public bool IsTerminalVisible => TerminalPaneHost.Visibility == Visibility.Visible;

        public double PersistedTerminalPanelHeight =>
            IsTerminalVisible && TerminalPanelRow.Height.Value > 0
                ? TerminalPanelRow.Height.Value
                : LastTerminalHeight;

        public void SetEditorSurfaceBackground(Windows.UI.Color color)
        {
            var brush = new SolidColorBrush(color);
            WorkspaceRoot.Background = brush;
            EditorSplitGrid.Background = brush;
            EditorPane1.Background = brush;
            EditorPane2.Background = brush;
            EditorTabView.Background = brush;
            EditorTabView2.Background = brush;
            ApplyEditorSurfaceToTabViewTemplate(EditorTabView, color);
            ApplyEditorSurfaceToTabViewTemplate(EditorTabView2, color);
        }

        public void DisableTabItemTransitions()
        {
            TryDisableTabItemTransitions(EditorTabView);
            TryDisableTabItemTransitions(EditorTabView2);
        }

        private static void TryDisableTabItemTransitions(TabView tabView)
        {
            try
            {
                tabView.ApplyTemplate();

                var queue = new Queue<DependencyObject>();
                queue.Enqueue(tabView);
                int visited = 0;

                while (queue.Count > 0 && visited < 256)
                {
                    visited++;
                    var current = queue.Dequeue();
                    if (current is UIElement element)
                    {
                        element.Transitions = new TransitionCollection();
                    }

                    if (current is ContentControl contentControl)
                    {
                        contentControl.ContentTransitions = new TransitionCollection();
                    }

                    if (current is ContentPresenter contentPresenter)
                    {
                        contentPresenter.ContentTransitions = new TransitionCollection();
                    }

                    if (current is Panel panel)
                    {
                        panel.ChildrenTransitions = new TransitionCollection();
                    }

                    if (current is ListViewBase listView &&
                        current.GetType().Name.Contains("TabViewListView", StringComparison.Ordinal))
                    {
                        listView.ItemContainerTransitions = new TransitionCollection();
                    }

                    int childCount;
                    try
                    {
                        childCount = VisualTreeHelper.GetChildrenCount(current);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int i = 0; i < childCount; i++)
                    {
                        try
                        {
                            queue.Enqueue(VisualTreeHelper.GetChild(current, i));
                        }
                        catch
                        {
                            // Some WinUI composition-backed elements are not stable to inspect during layout.
                        }
                    }
                }
            }
            catch
            {
                // Disabling an animation is best-effort; it must never affect editor startup.
            }
        }

        private static void ApplyEditorSurfaceToTabViewTemplate(TabView tabView, Windows.UI.Color color)
        {
            try
            {
                tabView.ApplyTemplate();
                var brush = new SolidColorBrush(color);
                var queue = new Queue<DependencyObject>();
                queue.Enqueue(tabView);
                int visited = 0;

                while (queue.Count > 0 && visited < 256)
                {
                    visited++;
                    var current = queue.Dequeue();

                    if (current is FrameworkElement fe &&
                        string.Equals(fe.Name, "TabContentPresenter", StringComparison.Ordinal) &&
                        current is ContentPresenter contentPresenter)
                    {
                        contentPresenter.Background = brush;
                    }

                    int childCount;
                    try
                    {
                        childCount = VisualTreeHelper.GetChildrenCount(current);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int i = 0; i < childCount; i++)
                    {
                        try
                        {
                            queue.Enqueue(VisualTreeHelper.GetChild(current, i));
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
                // Template coloring is best-effort and should never block editor creation.
            }
        }

        public TabView GetCurrentActiveTabView()
        {
            return ActiveTabView ?? EditorTabView;
        }

        public void RefreshSplitters()
        {
            foreach (var child in EditorSplitGrid.Children)
            {
                if (child is CustomSplitter cs)
                    cs.RefreshTheme();
            }
            if (TerminalSplitter is CustomSplitter terminalSplitter)
                terminalSplitter.RefreshTheme();
        }

        public void Localize(Func<string, string, string> getString)
        {
            string leftTooltip = getString("MoveTabLeftTooltip", "왼쪽 탭으로 이동 (Ctrl/Shift 누르고 클릭하면 탭 위치 이동)");
            string rightTooltip = getString("MoveTabRightTooltip", "오른쪽 탭으로 이동 (Ctrl/Shift 누르고 클릭하면 탭 위치 이동)");

            ToolTipService.SetToolTip(MoveTabLeftBtn, leftTooltip);
            ToolTipService.SetToolTip(MoveTabRightBtn, rightTooltip);
            ToolTipService.SetToolTip(MoveTab2LeftBtn, leftTooltip);
            ToolTipService.SetToolTip(MoveTab2RightBtn, rightTooltip);
        }

        public void SetSplitMode(EditorSplitMode mode, Action openNewTab)
        {
            CurrentSplitMode = mode;

            if (mode == EditorSplitMode.None)
            {
                EditorRow1.Height = new GridLength(1, GridUnitType.Star);
                EditorRow2.Height = new GridLength(0);
                EditorSplitterRow.Height = new GridLength(0);

                EditorColumn1.Width = new GridLength(1, GridUnitType.Star);
                EditorColumn2.Width = new GridLength(0);
                EditorSplitterColumn.Width = new GridLength(0);

                Grid.SetRow(EditorPane2, 0);
                Grid.SetColumn(EditorPane2, 2);

                EditorSplitter.Visibility = Visibility.Collapsed;
                EditorTabView2.Visibility = Visibility.Collapsed;

                while (EditorTabView2.TabItems.Count > 0)
                {
                    var item = (TabViewItem)EditorTabView2.TabItems[0];
                    EditorTabView2.TabItems.RemoveAt(0);
                    EditorTabView.TabItems.Add(item);
                }

                ActiveTabView = EditorTabView;
                if (EditorTabView.SelectedItem == null && EditorTabView.TabItems.Count > 0)
                {
                    EditorTabView.SelectedIndex = 0;
                }
            }
            else if (mode == EditorSplitMode.Vertical)
            {
                _isVerticalSplit = true;

                EditorRow1.Height = new GridLength(1, GridUnitType.Star);
                EditorRow2.Height = new GridLength(0);
                EditorSplitterRow.Height = new GridLength(0);

                EditorColumn1.Width = new GridLength(1, GridUnitType.Star);
                EditorColumn2.Width = new GridLength(1, GridUnitType.Star);
                EditorSplitterColumn.Width = new GridLength(4);

                Grid.SetRow(EditorSplitter, 0);
                Grid.SetRowSpan(EditorSplitter, 1);
                Grid.SetColumn(EditorSplitter, 1);
                Grid.SetColumnSpan(EditorSplitter, 1);
                EditorSplitter.Width = 4;
                EditorSplitter.Height = double.NaN;
                EditorSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                EditorSplitter.VerticalAlignment = VerticalAlignment.Stretch;

                Grid.SetRow(EditorPane2, 0);
                Grid.SetColumn(EditorPane2, 2);

                EditorSplitter.Visibility = Visibility.Visible;
                EditorTabView2.Visibility = Visibility.Visible;

                EnsureSecondPaneHasTab(openNewTab);
            }
            else if (mode == EditorSplitMode.Horizontal)
            {
                _isVerticalSplit = false;

                EditorRow1.Height = new GridLength(1, GridUnitType.Star);
                EditorRow2.Height = new GridLength(1, GridUnitType.Star);
                EditorSplitterRow.Height = new GridLength(4);

                EditorColumn1.Width = new GridLength(1, GridUnitType.Star);
                EditorColumn2.Width = new GridLength(0);
                EditorSplitterColumn.Width = new GridLength(0);

                Grid.SetRow(EditorSplitter, 1);
                Grid.SetRowSpan(EditorSplitter, 1);
                Grid.SetColumn(EditorSplitter, 0);
                Grid.SetColumnSpan(EditorSplitter, 1);
                EditorSplitter.Width = double.NaN;
                EditorSplitter.Height = 4;
                EditorSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                EditorSplitter.VerticalAlignment = VerticalAlignment.Stretch;

                Grid.SetRow(EditorPane2, 2);
                Grid.SetColumn(EditorPane2, 0);

                EditorSplitter.Visibility = Visibility.Visible;
                EditorTabView2.Visibility = Visibility.Visible;

                EnsureSecondPaneHasTab(openNewTab);
            }
        }

        public bool ToggleTerminal(Func<string> workingDirectoryProvider)
        {
            if (IsTerminalVisible)
            {
                HideTerminalPanel();
                return false;
            }

            EnsureTerminalPanelVisible();
            if (TerminalPane.HasSessions)
            {
                TerminalPane.ResumeNativeWindows();
                TerminalPane.QueueEmbeddedTerminalResize();
            }
            else
            {
                string workingDirectory = workingDirectoryProvider();
                if (string.IsNullOrWhiteSpace(workingDirectory) || !System.IO.Directory.Exists(workingDirectory))
                {
                    workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }

                TerminalPane.OpenTerminal(workingDirectory);
            }

            return true;
        }

        public bool HideTerminalPanelIfEmpty()
        {
            if (TerminalPane.HasSessions)
            {
                return false;
            }

            CollapseTerminalPanel();
            return true;
        }

        public void StopAllTerminalSessions()
        {
            TerminalPane.StopAllSessions();
        }

        private void EnsureSecondPaneHasTab(Action openNewTab)
        {
            if (EditorTabView2.TabItems.Count > 0)
            {
                return;
            }

            ActiveTabView = EditorTabView2;
            openNewTab();
        }

        private void EnsureTerminalPanelVisible()
        {
            _terminalPane.Visibility = Visibility.Visible;
            TerminalPaneHost.Visibility = Visibility.Visible;
            TerminalSplitter.Visibility = Visibility.Visible;
            TerminalSplitterRow.Height = new GridLength(TerminalSplitterVisibleThickness);
            LastTerminalHeight = Math.Clamp(LastTerminalHeight, 120, GetMaxTerminalPanelHeight());
            TerminalPanelRow.Height = new GridLength(LastTerminalHeight);
            TerminalPane.QueueEmbeddedTerminalResize();
        }

        private void HideTerminalPanel()
        {
            if (TerminalPane.HasSessions)
            {
                TerminalPane.SuspendNativeWindows();
            }

            CollapseTerminalPanel();
        }

        private void CollapseTerminalPanel()
        {
            _terminalPane.Visibility = Visibility.Collapsed;
            TerminalPaneHost.Visibility = Visibility.Collapsed;
            TerminalSplitter.Visibility = Visibility.Collapsed;
            TerminalSplitterRow.Height = new GridLength(0);
            TerminalPanelRow.Height = new GridLength(0);
        }

        private double GetMaxTerminalPanelHeight()
        {
            double availableHeight = ActualHeight;
            if (double.IsNaN(availableHeight) || availableHeight <= 0)
            {
                availableHeight = 600;
            }

            return Math.Max(160, availableHeight - 180);
        }

        private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsTerminalVisible)
            {
                TerminalPane.QueueEmbeddedTerminalResize();
            }
        }

        private void OnEditorSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is UIElement splitter)
            {
                _isDraggingEditorSplitter = true;
                _editorSplitterStartWidth = EditorColumn1.ActualWidth;
                _editorSplitterStartHeight = EditorRow1.ActualHeight;
                var pt = e.GetCurrentPoint(EditorSplitGrid).Position;
                _editorSplitterStartPointerX = pt.X;
                _editorSplitterStartPointerY = pt.Y;
                splitter.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnEditorSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingEditorSplitter && sender is UIElement)
            {
                var pt = e.GetCurrentPoint(EditorSplitGrid).Position;
                if (_isVerticalSplit)
                {
                    double deltaX = pt.X - _editorSplitterStartPointerX;
                    double newWidth = _editorSplitterStartWidth + deltaX;
                    double totalWidth = EditorSplitGrid.ActualWidth;
                    newWidth = Math.Clamp(newWidth, 100, totalWidth - 104);
                    double ratio = newWidth / (totalWidth - 4);
                    ratio = Math.Clamp(ratio, 0.05, 0.95);
                    EditorColumn1.Width = new GridLength(ratio, GridUnitType.Star);
                    EditorColumn2.Width = new GridLength(1.0 - ratio, GridUnitType.Star);
                }
                else
                {
                    double deltaY = pt.Y - _editorSplitterStartPointerY;
                    double newHeight = _editorSplitterStartHeight + deltaY;
                    double totalHeight = EditorSplitGrid.ActualHeight;
                    newHeight = Math.Clamp(newHeight, 100, totalHeight - 104);
                    double ratio = newHeight / (totalHeight - 4);
                    ratio = Math.Clamp(ratio, 0.05, 0.95);
                    EditorRow1.Height = new GridLength(ratio, GridUnitType.Star);
                    EditorRow2.Height = new GridLength(1.0 - ratio, GridUnitType.Star);
                }

                e.Handled = true;
            }
        }

        private void OnEditorSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingEditorSplitter && sender is UIElement splitter)
            {
                _isDraggingEditorSplitter = false;
                splitter.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnTerminalSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is UIElement splitter && TerminalPaneHost.Visibility == Visibility.Visible)
            {
                _isDraggingTerminalSplitter = true;
                _terminalSplitterStartHeight = TerminalPanelRow.Height.Value > 0 ? TerminalPanelRow.Height.Value : LastTerminalHeight;
                var pt = e.GetCurrentPoint(this).Position;
                _terminalSplitterStartPointerY = pt.Y;
                splitter.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnTerminalSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingTerminalSplitter)
            {
                var pt = e.GetCurrentPoint(this).Position;
                double deltaY = pt.Y - _terminalSplitterStartPointerY;
                double newHeight = Math.Clamp(_terminalSplitterStartHeight - deltaY, 120, GetMaxTerminalPanelHeight());
                LastTerminalHeight = newHeight;
                TerminalPanelRow.Height = new GridLength(newHeight);
                TerminalPane.QueueEmbeddedTerminalResize();
                e.Handled = true;
            }
        }

        private void OnTerminalSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingTerminalSplitter && sender is UIElement splitter)
            {
                _isDraggingTerminalSplitter = false;
                splitter.ReleasePointerCapture(e.Pointer);
                TerminalPane.QueueEmbeddedTerminalResize();
                TerminalPanelHeightChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void OnEditorTabViewAddTabClick(TabView sender, object args) => PrimaryAddTabButtonClick?.Invoke(sender, args);

        private void OnEditorTabViewTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) =>
            PrimaryTabCloseRequested?.Invoke(sender, args);

        private void OnEditorTabViewSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ActiveTabView = EditorTabView;
            PrimarySelectionChanged?.Invoke(sender, e);
        }

        private void OnEditorTabView2AddTabClick(TabView sender, object args)
        {
            ActiveTabView = sender;
            SecondaryAddTabButtonClick?.Invoke(sender, args);
        }

        private void OnEditorTabView2TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            ActiveTabView = sender;
            SecondaryTabCloseRequested?.Invoke(sender, args);
        }

        private void OnEditorTabView2SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ActiveTabView = EditorTabView2;
            SecondarySelectionChanged?.Invoke(sender, e);
        }

        private void OnTabViewGotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TabView tabView)
            {
                ActiveTabView = tabView;
            }

            TabViewGotFocus?.Invoke(sender, e);
        }

        private void OnMoveTabLeftClick(object sender, RoutedEventArgs e) => MoveTabLeftClick?.Invoke(sender, e);
        private void OnMoveTabRightClick(object sender, RoutedEventArgs e) => MoveTabRightClick?.Invoke(sender, e);
    }
}
