using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentPaneSessionMenuCallbacks
    {
        public Action<string>? HistorySelected { get; init; }
        public Action<string>? HistoryDeleted { get; init; }
        public Action<string>? OpenSessionSelected { get; init; }
        public Action<string>? OpenSessionClosed { get; init; }
    }

    internal sealed class AgentPaneSessionMenuCoordinator
    {
        private const int SessionButtonHeight = 28;
        private const int SessionRowHeight = 36;
        private const int SubagentIndent = 20;

        internal enum SessionTreeBranch { None, Parent, Child, LastChild }

        private readonly FrameworkElement _resourceOwner;
        private readonly StackPanel _historyListPanel;
        private readonly Flyout _historyFlyout;
        private readonly StackPanel _openSessionsListPanel;
        private readonly Flyout _openSessionsFlyout;
        private readonly Button _newSessionButton;
        private readonly TextBlock _newSessionButtonText;
        private readonly Border _newSessionBadge;
        private readonly TextBlock _newSessionBadgeText;
        private readonly Button _openSessionsButton;
        private readonly Button _rewindSessionButton;
        private readonly AgentPaneSessionMenuCallbacks _callbacks;
        private List<AgentHistoryItemViewModel> _historyItems = new List<AgentHistoryItemViewModel>();
        private string? _selectedHistoryId;
        private List<AgentOpenSessionItemViewModel> _openSessionItems = new List<AgentOpenSessionItemViewModel>();
        private Func<string, string, string> _getString = AgentDisplayLocalizer.CreateWithResourceLoader().GetString;
        private string _newSessionText = string.Empty;
        private bool _isBusy;
        private bool _openSessionMenuRefreshPending;
        private bool _historyMenuRefreshPending;
        private bool _openSessionCloseRequested;
        private bool _openSessionMenuLiveRefreshQueued;

        public AgentPaneSessionMenuCoordinator(
            FrameworkElement resourceOwner,
            StackPanel historyListPanel,
            Flyout historyFlyout,
            StackPanel openSessionsListPanel,
            Flyout openSessionsFlyout,
            Button newSessionButton,
            TextBlock newSessionButtonText,
            Border newSessionBadge,
            TextBlock newSessionBadgeText,
            Button openSessionsButton,
            Button rewindSessionButton,
            AgentPaneSessionMenuCallbacks callbacks)
        {
            _resourceOwner = resourceOwner;
            _historyListPanel = historyListPanel;
            _historyFlyout = historyFlyout;
            _openSessionsListPanel = openSessionsListPanel;
            _openSessionsFlyout = openSessionsFlyout;
            _newSessionButton = newSessionButton;
            _newSessionButtonText = newSessionButtonText;
            _newSessionBadge = newSessionBadge;
            _newSessionBadgeText = newSessionBadgeText;
            _openSessionsButton = openSessionsButton;
            _rewindSessionButton = rewindSessionButton;
            _callbacks = callbacks;
            _openSessionsFlyout.Closed += (_, _) =>
            {
                _openSessionCloseRequested = false;
                QueuePendingOpenSessionMenuRefresh();
            };
            _historyFlyout.Closed += (_, _) => QueuePendingHistoryMenuRefresh();
        }

        public void Localize(Func<string, string, string> getString, string newSessionText)
        {
            _getString = getString;
            _newSessionText = newSessionText;
            RebuildOpenSessionMenu();
            RebuildHistoryMenu();
        }

        public void SetBusy(bool isBusy)
        {
            _isBusy = isBusy;
            RebuildHistoryMenu();
        }

        public void UpdateHistoryItems(List<AgentHistoryItemViewModel> items, string? selectedId)
        {
            _historyItems = items;
            _selectedHistoryId = selectedId;
            RebuildHistoryMenu();
        }

        public void UpdateOpenSessionItems(List<AgentOpenSessionItemViewModel> items)
        {
            _openSessionItems = items;
            RebuildOpenSessionMenu();
        }

        internal IReadOnlyList<AgentOpenSessionItemViewModel> OpenSessionItems => _openSessionItems;

        public void RebuildOpenSessionMenu()
        {
            int completedCount = _openSessionItems.Sum(item => Math.Max(0, item.CompletedNotificationCount));
            UpdateOpenSessionButtonChrome(completedCount);

            // Background sessions can complete while this popup is processing input or
            // layout. Keep its visual tree stable until WinUI has fully closed the flyout.
            // Exception: when the user closes a session from this list, refresh the list
            // in place so the remaining sessions can be closed one after another.
            if (_openSessionsFlyout.IsOpen)
            {
                _openSessionMenuRefreshPending = true;
                if (_openSessionCloseRequested)
                {
                    QueueLiveOpenSessionMenuRefresh();
                }

                return;
            }

            _openSessionMenuRefreshPending = false;
            RebuildOpenSessionMenuCore();
        }

        private void RebuildOpenSessionMenuCore()
        {
            _openSessionsListPanel.Children.Clear();
            Style? buttonStyle = _resourceOwner.Resources["AgentButtonStyle"] as Style;

            if (_openSessionItems.Count == 0)
            {
                _openSessionsListPanel.Children.Add(new TextBlock
                {
                    Text = _getString("AgentOpenSessionsEmptyText", "열린 세션 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            Style? treeLineStyle = _resourceOwner.Resources["AgentSessionTreeLineStyle"] as Style;
            foreach (var (item, branch) in GetSessionTreeRows(_openSessionItems, item => item.Id, item => item.OrchestratorSessionId))
            {
                bool isChild = branch is SessionTreeBranch.Child or SessionTreeBranch.LastChild;
                var rowGrid = new Grid
                {
                    ColumnSpacing = 4,
                    Height = SessionRowHeight,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                AddSessionTreeLines(rowGrid, branch, treeLineStyle);

                var titlePanel = new Grid
                {
                    ColumnSpacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                titlePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                titlePanel.Children.Add(new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(item.CompletedNotificationCount > 0
                        ? Windows.UI.Color.FromArgb(255, 220, 38, 38)
                        : (item.IsRunning
                            ? Windows.UI.Color.FromArgb(255, 34, 197, 94)
                            : Windows.UI.Color.FromArgb(255, 156, 163, 175))),
                    VerticalAlignment = VerticalAlignment.Center
                });

                if (item.IsSelected)
                {
                    titlePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var selectedIcon = new FontIcon
                    {
                        Glyph = "\uE73E",
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(selectedIcon, 1);
                    titlePanel.Children.Add(selectedIcon);
                }

                var titleText = new TextBlock
                {
                    Text = item.Title,
                    FontSize = 11,
                    MaxWidth = 190,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(titleText, titlePanel.ColumnDefinitions.Count);
                titlePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titlePanel.Children.Add(titleText);

                string currentId = item.Id;
                var selectButton = new Button
                {
                    Content = titlePanel,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                    Height = SessionButtonHeight,
                    Margin = new Thickness(isChild ? SubagentIndent : 0, 0, 0, 0),
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle,
                    IsEnabled = item.CanSelect
                };
                if (item.IsSelected)
                {
                    selectButton.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }
                selectButton.Click += (_, _) =>
                {
                    _openSessionsFlyout.Hide();
                    _callbacks.OpenSessionSelected?.Invoke(currentId);
                };
                Grid.SetColumn(selectButton, 0);
                rowGrid.Children.Add(selectButton);

                var closeButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = SessionButtonHeight,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0),
                    Style = buttonStyle,
                    IsEnabled = item.CanClose
                };
                ToolTipService.SetToolTip(closeButton, _getString("AgentOpenSessionCloseText", "세션 닫기"));
                closeButton.Click += (_, _) =>
                {
                    // Keep the flyout open so the user can close several sessions in a row.
                    _openSessionCloseRequested = true;
                    _callbacks.OpenSessionClosed?.Invoke(currentId);
                };
                Grid.SetColumn(closeButton, 1);
                rowGrid.Children.Add(closeButton);
                _openSessionsListPanel.Children.Add(rowGrid);
            }
        }

        internal static IEnumerable<(T Item, SessionTreeBranch Branch)> GetSessionTreeRows<T>(
            IReadOnlyList<T> items, Func<T, string> getId, Func<T, string?> getParentId)
        {
            var parentIds = items
                .Where(item => string.IsNullOrWhiteSpace(getParentId(item)))
                .Select(getId)
                .ToHashSet(StringComparer.Ordinal);
            var childrenByParent = items
                .Where(item => getParentId(item) is string parentId && parentIds.Contains(parentId))
                .ToLookup(getParentId, StringComparer.Ordinal);

            foreach (var item in items)
            {
                if (getParentId(item) is string parentId && parentIds.Contains(parentId))
                {
                    continue;
                }

                var children = childrenByParent[getId(item)].ToArray();
                yield return (item, children.Length == 0 ? SessionTreeBranch.None : SessionTreeBranch.Parent);
                for (int i = 0; i < children.Length; i++)
                {
                    yield return (children[i], i == children.Length - 1 ? SessionTreeBranch.LastChild : SessionTreeBranch.Child);
                }
            }
        }

        private static void AddSessionTreeLines(Grid row, SessionTreeBranch branch, Style? lineStyle)
        {
            if (branch == SessionTreeBranch.None)
            {
                return;
            }

            const int stemLeft = 12;
            const int rowCenter = SessionRowHeight / 2;
            var stem = new Rectangle
            {
                Style = lineStyle,
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(stemLeft, 0, 0, 0)
            };
            if (branch == SessionTreeBranch.Parent)
            {
                stem.Height = (SessionRowHeight - SessionButtonHeight) / 2;
                stem.VerticalAlignment = VerticalAlignment.Bottom;
                row.Children.Add(stem);
                return;
            }

            if (branch == SessionTreeBranch.LastChild)
            {
                stem.Height = rowCenter + 1;
                stem.VerticalAlignment = VerticalAlignment.Top;
            }

            row.Children.Add(stem);
            row.Children.Add(new Rectangle
            {
                Style = lineStyle,
                Width = SubagentIndent - stemLeft,
                Height = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(stemLeft, rowCenter, 0, 0)
            });
        }

        public void RebuildHistoryMenu()
        {
            if (_historyFlyout.IsOpen)
            {
                _historyMenuRefreshPending = true;
                return;
            }

            _historyMenuRefreshPending = false;
            RebuildHistoryMenuCore();
        }

        private void RebuildHistoryMenuCore()
        {
            _historyListPanel.Children.Clear();
            Style? buttonStyle = _resourceOwner.Resources["AgentButtonStyle"] as Style;

            if (_historyItems.Count == 0)
            {
                _historyListPanel.Children.Add(new TextBlock
                {
                    Text = _getString("AgentHistoryEmptyText", "히스토리 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            Style? treeLineStyle = _resourceOwner.Resources["AgentSessionTreeLineStyle"] as Style;
            foreach (var (item, branch) in GetSessionTreeRows(_historyItems, item => item.Id, item => item.OrchestratorSessionId))
            {
                bool isChild = branch is SessionTreeBranch.Child or SessionTreeBranch.LastChild;
                var rowGrid = new Grid { ColumnSpacing = 4, Height = SessionRowHeight, Margin = new Thickness(0, 0, 10, 0) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                AddSessionTreeLines(rowGrid, branch, treeLineStyle);

                bool isSelected = string.Equals(_selectedHistoryId, item.Id, StringComparison.Ordinal);
                string currentId = item.Id;
                string sessionTitle = item.IsMissingOrchestrator
                    ? AgentSessionLabels.Prefix(AgentSessionRole.Orchestrator) +
                        _getString("AgentHistoryMissingOrchestrator", "오케스트레이터 · 대화 기록 없음")
                    : item.Title;
                string title = $"{sessionTitle} ({item.TimeText})";
                var selectButton = new Button
                {
                    Content = new TextBlock
                    {
                        Text = isSelected ? $"✓ {title}" : title,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Height = SessionButtonHeight,
                    Margin = new Thickness(isChild ? SubagentIndent : 0, 0, 0, 0),
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                AutomationProperties.SetName(selectButton, title);
                ToolTipService.SetToolTip(selectButton, title);
                if (isSelected)
                {
                    selectButton.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }
                selectButton.Click += (_, _) =>
                {
                    _historyFlyout.Hide();
                    _callbacks.HistorySelected?.Invoke(currentId);
                };
                Grid.SetColumn(selectButton, 0);
                rowGrid.Children.Add(selectButton);

                var deleteButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = SessionButtonHeight,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0),
                    Style = buttonStyle,
                    Visibility = item.IsMissingOrchestrator ? Visibility.Collapsed : Visibility.Visible,
                    IsEnabled = !_isBusy
                };
                ToolTipService.SetToolTip(deleteButton, _getString("AgentHistoryDeleteText", "삭제"));
                deleteButton.Click += (_, _) => _callbacks.HistoryDeleted?.Invoke(currentId);
                Grid.SetColumn(deleteButton, 1);
                rowGrid.Children.Add(deleteButton);
                _historyListPanel.Children.Add(rowGrid);
            }
        }

        private void QueuePendingOpenSessionMenuRefresh()
        {
            if (!_openSessionMenuRefreshPending)
            {
                return;
            }

            _resourceOwner.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                RebuildOpenSessionMenu);
        }

        // The flyout stays open while sessions are closed, so its list must be
        // refreshed in place. Queue at low priority so WinUI finishes the click
        // input and layout work before the visual tree changes.
        private void QueueLiveOpenSessionMenuRefresh()
        {
            if (_openSessionMenuLiveRefreshQueued)
            {
                return;
            }

            _openSessionMenuLiveRefreshQueued = true;
            _resourceOwner.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    _openSessionMenuLiveRefreshQueued = false;
                    _openSessionMenuRefreshPending = false;
                    RebuildOpenSessionMenuCore();
                });
        }

        private void QueuePendingHistoryMenuRefresh()
        {
            if (!_historyMenuRefreshPending)
            {
                return;
            }

            _resourceOwner.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                RebuildHistoryMenu);
        }

        private void UpdateOpenSessionButtonChrome(int completedCount)
        {
            bool showSessionList = _openSessionItems.Count > 1;
            _openSessionsButton.Visibility = showSessionList ? Visibility.Visible : Visibility.Collapsed;
            string newSessionText = string.IsNullOrWhiteSpace(_newSessionText) ? "새 세션" : _newSessionText;
            _newSessionButtonText.Text = newSessionText;

            if (completedCount > 0)
            {
                _newSessionBadgeText.Text = completedCount.ToString();
                _newSessionBadge.Visibility = Visibility.Visible;
            }
            else
            {
                _newSessionBadge.Visibility = Visibility.Collapsed;
            }

            string automationName = completedCount > 0
                ? newSessionText + " " + completedCount
                : newSessionText;
            AutomationProperties.SetName(_newSessionButton, automationName);
            string? completionTooltip = completedCount > 0
                ? string.Format(
                    _getString("AgentCompletedSessionsBadgeTooltip", "{0:N0} background session(s) completed"),
                    completedCount)
                : null;
            ToolTipService.SetToolTip(_newSessionButton, completionTooltip);
            _newSessionButton.CornerRadius = showSessionList
                ? new CornerRadius(4, 0, 0, 4)
                : new CornerRadius(4);
            _openSessionsButton.CornerRadius = new CornerRadius(0, 4, 4, 0);
            _rewindSessionButton.CornerRadius = new CornerRadius(4);
        }
    }
}
