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

        public void RebuildOpenSessionMenu()
        {
            int completedCount = _openSessionItems.Sum(item => Math.Max(0, item.CompletedNotificationCount));
            UpdateOpenSessionButtonChrome(completedCount);
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

            foreach (var item in _openSessionItems)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titlePanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
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
                    titlePanel.Children.Add(new FontIcon
                    {
                        Glyph = "\uE73E",
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                titlePanel.Children.Add(new TextBlock
                {
                    Text = item.Title,
                    FontSize = 11,
                    MaxWidth = 190,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center
                });

                string currentId = item.Id;
                var selectButton = new Button
                {
                    Content = titlePanel,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
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
                    _callbacks.OpenSessionSelected?.Invoke(currentId);
                    _openSessionsFlyout.Hide();
                };
                Grid.SetColumn(selectButton, 0);
                rowGrid.Children.Add(selectButton);

                var closeButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle,
                    IsEnabled = item.CanClose
                };
                ToolTipService.SetToolTip(closeButton, _getString("AgentOpenSessionCloseText", "세션 닫기"));
                closeButton.Click += (_, _) => _callbacks.OpenSessionClosed?.Invoke(currentId);
                Grid.SetColumn(closeButton, 1);
                rowGrid.Children.Add(closeButton);
                _openSessionsListPanel.Children.Add(rowGrid);
            }
        }

        public void RebuildHistoryMenu()
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

            foreach (var item in _historyItems)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isSelected = string.Equals(_selectedHistoryId, item.Id, StringComparison.Ordinal);
                string currentId = item.Id;
                var selectButton = new Button
                {
                    Content = isSelected ? $"✓ {item.Title} ({item.TimeText})" : $"{item.Title} ({item.TimeText})",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                if (isSelected)
                {
                    selectButton.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }
                selectButton.Click += (_, _) =>
                {
                    _callbacks.HistorySelected?.Invoke(currentId);
                    _historyFlyout.Hide();
                };
                Grid.SetColumn(selectButton, 0);
                rowGrid.Children.Add(selectButton);

                var deleteButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle,
                    IsEnabled = !_isBusy
                };
                ToolTipService.SetToolTip(deleteButton, _getString("AgentHistoryDeleteText", "삭제"));
                deleteButton.Click += (_, _) => _callbacks.HistoryDeleted?.Invoke(currentId);
                Grid.SetColumn(deleteButton, 1);
                rowGrid.Children.Add(deleteButton);
                _historyListPanel.Children.Add(rowGrid);
            }
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
