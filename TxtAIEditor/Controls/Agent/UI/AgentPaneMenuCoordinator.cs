using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentPaneMenuCallbacks
    {
        public Action<string>? AgentPresetToggled { get; init; }
        public Action<string>? AgentPresetEdited { get; init; }
        public Action<string>? AgentPresetDeleted { get; init; }
        public Action<string>? AgentPresetRemoved { get; init; }
        public Action<string>? AgentSkillToggled { get; init; }
        public Action<string>? AgentSkillRemoved { get; init; }
        public Action<string>? AgentMcpToggled { get; init; }
        public Action<string>? AgentMcpEdited { get; init; }
        public Action<string>? AgentMcpSettingsRequested { get; init; }
        public Action<string>? AgentMcpDeleted { get; init; }
        public Action<string>? AgentMcpRemoved { get; init; }
    }

    internal sealed class AgentPaneMenuCoordinator
    {
        private const double SelectedChipScrollStep = 160;

        private readonly FrameworkElement _resourceOwner;
        private readonly StackPanel _agentMcpListPanel;
        private readonly Flyout _agentMcpFlyout;
        private readonly StackPanel _agentSkillListPanel;
        private readonly StackPanel _agentPresetListPanel;
        private readonly Flyout _agentPresetFlyout;
        private readonly ScrollViewer _agentSelectedPresetScrollViewer;
        private readonly StackPanel _agentSelectedPresetPanel;
        private readonly StackPanel _agentSelectedPresetScrollButtons;
        private readonly Button _agentSelectedPresetScrollLeftButton;
        private readonly Button _agentSelectedPresetScrollRightButton;
        private readonly AgentPaneMenuCallbacks _callbacks;
        private readonly Dictionary<string, Button> _agentSkillButtons = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _agentPresetNames = new();
        private HashSet<string> _selectedAgentPresetNames = new(StringComparer.OrdinalIgnoreCase);
        private List<AgentSkillItem> _agentSkillItems = new();
        private HashSet<string> _selectedAgentSkillNames = new(StringComparer.OrdinalIgnoreCase);
        private string _agentSkillFilter = string.Empty;
        private List<AgentMcpItem> _agentMcpItems = new();
        private HashSet<string> _selectedAgentMcpNames = new(StringComparer.OrdinalIgnoreCase);
        private Func<string, string, string>? _getString;

        public AgentPaneMenuCoordinator(
            FrameworkElement resourceOwner,
            StackPanel agentMcpListPanel,
            Flyout agentMcpFlyout,
            StackPanel agentSkillListPanel,
            StackPanel agentPresetListPanel,
            Flyout agentPresetFlyout,
            ScrollViewer agentSelectedPresetScrollViewer,
            StackPanel agentSelectedPresetPanel,
            StackPanel agentSelectedPresetScrollButtons,
            Button agentSelectedPresetScrollLeftButton,
            Button agentSelectedPresetScrollRightButton,
            AgentPaneMenuCallbacks callbacks)
        {
            _resourceOwner = resourceOwner;
            _agentMcpListPanel = agentMcpListPanel;
            _agentMcpFlyout = agentMcpFlyout;
            _agentSkillListPanel = agentSkillListPanel;
            _agentPresetListPanel = agentPresetListPanel;
            _agentPresetFlyout = agentPresetFlyout;
            _agentSelectedPresetScrollViewer = agentSelectedPresetScrollViewer;
            _agentSelectedPresetPanel = agentSelectedPresetPanel;
            _agentSelectedPresetScrollButtons = agentSelectedPresetScrollButtons;
            _agentSelectedPresetScrollLeftButton = agentSelectedPresetScrollLeftButton;
            _agentSelectedPresetScrollRightButton = agentSelectedPresetScrollRightButton;
            _callbacks = callbacks;
        }

        public void Localize(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        public void RebuildAll()
        {
            RebuildAgentMcpMenu();
            RebuildAgentSkillMenu();
            RebuildAgentPresetMenu();
            RebuildSelectedAgentPresetChips();
        }

        public void UpdateAgentPresetsMenu(
            IReadOnlyList<string> presetNames,
            IReadOnlyCollection<string> selectedPresetNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _agentPresetNames = new List<string>(presetNames);
            _selectedAgentPresetNames = new HashSet<string>(selectedPresetNames, StringComparer.OrdinalIgnoreCase);
            RebuildAgentPresetMenu();
            RebuildSelectedAgentPresetChips();
        }

        public void UpdateAgentSkillsMenu(
            IReadOnlyList<AgentSkillItem> skills,
            IReadOnlyCollection<string> selectedSkillNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _agentSkillItems = new List<AgentSkillItem>(skills);
            _selectedAgentSkillNames = new HashSet<string>(selectedSkillNames, StringComparer.OrdinalIgnoreCase);
            RebuildAgentSkillMenu();
            RebuildSelectedAgentPresetChips();
        }

        public void UpdateAgentSkillSelection(
            IReadOnlyCollection<string> selectedSkillNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _selectedAgentSkillNames = new HashSet<string>(selectedSkillNames, StringComparer.OrdinalIgnoreCase);
            UpdateAgentSkillButtonStates();
            RebuildSelectedAgentPresetChips();
        }

        public void SetAgentSkillFilter(string? filter)
        {
            _agentSkillFilter = filter?.Trim() ?? string.Empty;
            RebuildAgentSkillMenu();
        }

        public void UpdateAgentMcpMenu(
            IReadOnlyList<AgentMcpItem> mcpItems,
            IReadOnlyCollection<string> selectedMcpNames,
            Func<string, string, string> getString)
        {
            _getString = getString;
            _agentMcpItems = new List<AgentMcpItem>(mcpItems);
            _selectedAgentMcpNames = new HashSet<string>(selectedMcpNames, StringComparer.OrdinalIgnoreCase);
            RebuildAgentMcpMenu();
            RebuildSelectedAgentPresetChips();
        }

        public void RebuildAgentMcpMenu()
        {
            if (_agentMcpListPanel == null)
            {
                return;
            }

            _agentMcpListPanel.Children.Clear();
            Style? buttonStyle = _resourceOwner.Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = GetString;

            if (_agentMcpItems.Count == 0)
            {
                _agentMcpListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentMcpEmptyText", "등록된 MCP 없음"),
                    FontSize = 11,
                    Foreground = CreateMcpEmptyTextBrush(),
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (var item in _agentMcpItems)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isSelected = _selectedAgentMcpNames.Contains(item.Name);
                var selectBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    MinHeight = 34,
                    Padding = new Thickness(8, 3, 8, 3),
                    Style = buttonStyle
                };

                var textStack = new StackPanel { Spacing = 1 };
                textStack.Children.Add(new TextBlock
                {
                    Text = isSelected ? $"✓ {item.Name}" : item.Name,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                });
                textStack.Children.Add(new TextBlock
                {
                    Text = item.Detail,
                    FontSize = 10,
                    Foreground = CreateMcpSecondaryTextBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                });
                selectBtn.Content = textStack;
                string itemTooltip = string.IsNullOrWhiteSpace(item.Detail)
                    ? item.Name
                    : $"{item.Name}\n{item.Detail}";
                ToolTipService.SetToolTip(selectBtn, itemTooltip);

                string currentName = item.Name;
                selectBtn.Click += (_, _) =>
                {
                    _callbacks.AgentMcpToggled?.Invoke(currentName);
                };
                Grid.SetColumn(selectBtn, 0);
                rowGrid.Children.Add(selectBtn);

                if (!item.CanEdit && !item.CanDelete)
                {
                    var settingsBtn = new Button
                    {
                        Content = new FontIcon { Glyph = "\uE713", FontSize = 10 },
                        Width = 28,
                        Height = 34,
                        Padding = new Thickness(0),
                        Style = buttonStyle
                    };
                    ToolTipService.SetToolTip(settingsBtn, getString("AgentMcpBuiltInSettingsTooltip", "내장 플러그인 설정"));
                    settingsBtn.Click += (_, _) =>
                    {
                        _callbacks.AgentMcpSettingsRequested?.Invoke(currentName);
                        _agentMcpFlyout.Hide();
                    };
                    Grid.SetColumn(settingsBtn, 2);
                    rowGrid.Children.Add(settingsBtn);
                    _agentMcpListPanel.Children.Add(rowGrid);
                    continue;
                }

                var editBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                    Width = 28,
                    Height = 34,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(editBtn, getString("AgentMcpEditText", "수정"));
                editBtn.IsEnabled = item.CanEdit;
                editBtn.Click += (_, _) =>
                {
                    _callbacks.AgentMcpEdited?.Invoke(currentName);
                    _agentMcpFlyout.Hide();
                };
                Grid.SetColumn(editBtn, 1);
                rowGrid.Children.Add(editBtn);

                var deleteBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 34,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(deleteBtn, getString("AgentMcpDeleteText", "삭제"));
                deleteBtn.IsEnabled = item.CanDelete;
                deleteBtn.Click += (_, _) => _callbacks.AgentMcpDeleted?.Invoke(currentName);
                Grid.SetColumn(deleteBtn, 2);
                rowGrid.Children.Add(deleteBtn);

                _agentMcpListPanel.Children.Add(rowGrid);
            }
        }

        public void RebuildAgentSkillMenu()
        {
            if (_agentSkillListPanel == null)
            {
                return;
            }

            _agentSkillListPanel.Children.Clear();
            _agentSkillButtons.Clear();
            Style? buttonStyle = _resourceOwner.Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = GetString;

            if (_agentSkillItems.Count == 0)
            {
                _agentSkillListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentSkillEmptyText", "설치된 스킬 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            int visibleSkillCount = 0;
            foreach (var skill in _agentSkillItems)
            {
                if (_agentSkillFilter.Length > 0 &&
                    !skill.Name.Contains(_agentSkillFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                visibleSkillCount++;
                bool isSelected = _selectedAgentSkillNames.Contains(skill.Name);
                var selectBtn = new Button
                {
                    Content = isSelected ? $"✓ {skill.Name}" : skill.Name,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                string currentName = skill.Name;
                selectBtn.Click += (_, _) => _callbacks.AgentSkillToggled?.Invoke(currentName);
                _agentSkillButtons[currentName] = selectBtn;
                _agentSkillListPanel.Children.Add(selectBtn);
            }

            if (visibleSkillCount == 0)
            {
                _agentSkillListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentSkillFilterEmptyText", "검색 결과 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
            }
        }

        public void RebuildAgentPresetMenu()
        {
            if (_agentPresetListPanel == null)
            {
                return;
            }

            _agentPresetListPanel.Children.Clear();
            Style? buttonStyle = _resourceOwner.Resources["AgentButtonStyle"] as Style;
            Func<string, string, string> getString = GetString;

            if (_agentPresetNames.Count == 0)
            {
                _agentPresetListPanel.Children.Add(new TextBlock
                {
                    Text = getString("AgentPresetEmptyText", "프리셋 없음"),
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"],
                    Margin = new Thickness(4, 2, 4, 2)
                });
                return;
            }

            foreach (string presetName in _agentPresetNames)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                bool isSelected = _selectedAgentPresetNames.Contains(presetName);
                var selectBtn = new Button
                {
                    Content = isSelected ? $"✓ {presetName}" : presetName,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                string currentName = presetName;
                selectBtn.Click += (_, _) => _callbacks.AgentPresetToggled?.Invoke(currentName);
                Grid.SetColumn(selectBtn, 0);
                rowGrid.Children.Add(selectBtn);

                var editBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(editBtn, getString("AgentPresetEditText", "수정"));
                editBtn.Click += (_, _) =>
                {
                    _callbacks.AgentPresetEdited?.Invoke(currentName);
                    _agentPresetFlyout.Hide();
                };
                Grid.SetColumn(editBtn, 1);
                rowGrid.Children.Add(editBtn);

                var deleteBtn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                ToolTipService.SetToolTip(deleteBtn, getString("AgentPresetDeleteText", "삭제"));
                deleteBtn.Click += (_, _) => _callbacks.AgentPresetDeleted?.Invoke(currentName);
                Grid.SetColumn(deleteBtn, 2);
                rowGrid.Children.Add(deleteBtn);

                _agentPresetListPanel.Children.Add(rowGrid);
            }
        }

        public void RebuildSelectedAgentPresetChips()
        {
            if (_agentSelectedPresetPanel == null)
            {
                return;
            }

            _agentSelectedPresetPanel.Children.Clear();
            Func<string, string, string> getString = GetString;

            foreach (string presetName in _agentPresetNames)
            {
                if (!_selectedAgentPresetNames.Contains(presetName))
                {
                    continue;
                }

                _agentSelectedPresetPanel.Children.Add(CreateSelectedChip(
                    presetName,
                    getString("AgentPresetRemoveTooltip", "선택 해제"),
                    () => _callbacks.AgentPresetRemoved?.Invoke(presetName)));
            }

            string mcpPrefix = getString("AgentMcpChipPrefix", "MCP: ");
            foreach (var mcp in _agentMcpItems)
            {
                if (!_selectedAgentMcpNames.Contains(mcp.Name))
                {
                    continue;
                }

                string currentName = mcp.Name;
                _agentSelectedPresetPanel.Children.Add(CreateSelectedChip(
                    mcpPrefix + mcp.Name,
                    getString("AgentMcpRemoveTooltip", "MCP 선택 해제"),
                    () => _callbacks.AgentMcpRemoved?.Invoke(currentName)));
            }

            string skillPrefix = getString("AgentSkillChipPrefix", "Skill: ");
            var selectedSkillNames = new List<string>(_selectedAgentSkillNames);
            selectedSkillNames.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (string skillName in selectedSkillNames)
            {
                string currentName = skillName;
                _agentSelectedPresetPanel.Children.Add(CreateSelectedChip(
                    skillPrefix + skillName,
                    getString("AgentSkillRemoveTooltip", "스킬 선택 해제"),
                    () => _callbacks.AgentSkillRemoved?.Invoke(currentName)));
            }

            _agentSelectedPresetScrollViewer.Visibility =
                _agentSelectedPresetPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            QueueSelectedChipScrollButtonsUpdate();
        }

        public void OnSelectedPresetScrollViewerViewChanged()
        {
            UpdateSelectedChipScrollButtons();
        }

        public void OnSelectedPresetScrollViewerSizeChanged()
        {
            QueueSelectedChipScrollButtonsUpdate();
        }

        public void OnSelectedPresetPanelSizeChanged()
        {
            QueueSelectedChipScrollButtonsUpdate();
        }

        public void OnSelectedPresetScrollLeftClick()
        {
            ScrollSelectedChips(-SelectedChipScrollStep);
        }

        public void OnSelectedPresetScrollRightClick()
        {
            ScrollSelectedChips(SelectedChipScrollStep);
        }

        private Brush CreateMcpEmptyTextBrush()
        {
            return CreateMcpSecondaryTextBrush();
        }

        private Brush CreateMcpSecondaryTextBrush()
        {
            bool isLightTheme = _resourceOwner.ActualTheme == ElementTheme.Light ||
                (_resourceOwner.ActualTheme == ElementTheme.Default &&
                    Application.Current.RequestedTheme == ApplicationTheme.Light);

            return new SolidColorBrush(isLightTheme
                ? Windows.UI.Color.FromArgb(255, 75, 85, 99)
                : Windows.UI.Color.FromArgb(255, 229, 231, 235));
        }

        private void UpdateAgentSkillButtonStates()
        {
            foreach (var pair in _agentSkillButtons)
            {
                bool isSelected = _selectedAgentSkillNames.Contains(pair.Key);
                pair.Value.Content = isSelected ? $"✓ {pair.Key}" : pair.Key;
            }
        }

        private void ScrollSelectedChips(double delta)
        {
            if (_agentSelectedPresetScrollViewer.Visibility != Visibility.Visible)
            {
                return;
            }

            double targetOffset = Math.Clamp(
                _agentSelectedPresetScrollViewer.HorizontalOffset + delta,
                0,
                _agentSelectedPresetScrollViewer.ScrollableWidth);
            _agentSelectedPresetScrollViewer.ChangeView(targetOffset, null, null, false);
            UpdateSelectedChipScrollButtons();
        }

        private void QueueSelectedChipScrollButtonsUpdate()
        {
            if (_resourceOwner.DispatcherQueue?.TryEnqueue(UpdateSelectedChipScrollButtons) == true)
            {
                return;
            }

            UpdateSelectedChipScrollButtons();
        }

        private void UpdateSelectedChipScrollButtons()
        {
            bool hasOverflow =
                _agentSelectedPresetScrollViewer.Visibility == Visibility.Visible &&
                _agentSelectedPresetPanel.Children.Count > 0 &&
                _agentSelectedPresetScrollViewer.ScrollableWidth > 0.5;

            _agentSelectedPresetScrollButtons.Visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
            if (!hasOverflow)
            {
                return;
            }

            _agentSelectedPresetScrollLeftButton.IsEnabled = _agentSelectedPresetScrollViewer.HorizontalOffset > 0.5;
            _agentSelectedPresetScrollRightButton.IsEnabled =
                _agentSelectedPresetScrollViewer.HorizontalOffset < _agentSelectedPresetScrollViewer.ScrollableWidth - 0.5;
        }

        private Border CreateSelectedChip(string text, string tooltip, Action removeAction)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontSize = 11,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var removeBtn = new Button
            {
                Content = "×",
                Width = 18,
                Height = 18,
                MinHeight = 18,
                MinWidth = 18,
                FontSize = 9,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            ToolTipService.SetToolTip(removeBtn, tooltip);
            removeBtn.Click += (_, _) => removeAction();

            var chipContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            chipContent.Children.Add(textBlock);
            chipContent.Children.Add(removeBtn);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 0, 4, 0),
                BorderThickness = new Thickness(1),
                MinHeight = 24,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
                UseLayoutRounding = true,
                Child = chipContent
            };

            ApplySelectedChipTheme(chip, textBlock, removeBtn);
            chip.ActualThemeChanged += (_, _) => ApplySelectedChipTheme(chip, textBlock, removeBtn);

            return chip;
        }

        private static void ApplySelectedChipTheme(Border chip, TextBlock textBlock, Button removeBtn)
        {
            chip.Background = (Brush)Application.Current.Resources["AccentButtonBackground"];
            chip.BorderBrush = (Brush)Application.Current.Resources["AccentButtonBackground"];
            textBlock.Foreground = (Brush)Application.Current.Resources["SystemControlForegroundChromeWhiteBrush"];
            removeBtn.Foreground = (Brush)Application.Current.Resources["SystemControlForegroundChromeWhiteBrush"];
        }

        private string GetString(string key, string fallback)
        {
            return _getString?.Invoke(key, fallback) ?? fallback;
        }
    }
}
