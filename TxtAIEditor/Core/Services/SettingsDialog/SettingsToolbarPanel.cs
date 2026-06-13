using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsToolbarPanel : UserControl
    {
        private sealed record ToolbarOrderItem(string Id, string Label);

        private readonly CheckBox _showLabelsCheck;
        private readonly IReadOnlyList<ToolbarButtonOption> _toolbarOptions;
        private readonly List<CheckBox> _visibilityChecks;
        private readonly ObservableCollection<ToolbarOrderItem> _leftOrderItems;
        private readonly ObservableCollection<ToolbarOrderItem> _rightOrderItems;

        public SettingsToolbarPanel(EditorSettings settings, Func<string, string, string> getString)
        {
            _toolbarOptions = ToolbarButtonCatalog.All.ToList();
            _visibilityChecks = new List<CheckBox>();
            _showLabelsCheck = new CheckBox { Content = getString("SettingsToolbarShowLabels", "툴바 버튼 글자 표시"), IsChecked = settings.ToolbarShowLabels };

            var defaultOrder = NormalizeToolbarOrder(settings.ToolbarButtonOrder);
            var leftAlignedIds = new HashSet<string>(
                NormalizeToolbarLeftAlignedButtons(settings.ToolbarLeftAlignedButtons),
                StringComparer.OrdinalIgnoreCase);
            var orderedOptions = defaultOrder
                .Select(id => _toolbarOptions.First(option => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            _leftOrderItems = new ObservableCollection<ToolbarOrderItem>(orderedOptions
                .Where(option => leftAlignedIds.Contains(option.Id))
                .Select(option => new ToolbarOrderItem(option.Id, getString(option.ResourceKey, option.Id))));
            _rightOrderItems = new ObservableCollection<ToolbarOrderItem>(orderedOptions
                .Where(option => !leftAlignedIds.Contains(option.Id))
                .Select(option => new ToolbarOrderItem(option.Id, getString(option.ResourceKey, option.Id))));

            Content = CreateSection(settings, getString);
        }

        public void ApplyToSettings(EditorSettings settings)
        {
            settings.ToolbarShowLabels = _showLabelsCheck.IsChecked == true;
            settings.ToolbarButtonOrder = _leftOrderItems
                .Concat(_rightOrderItems)
                .Select(item => item.Id)
                .ToList();
            settings.ToolbarLeftAlignedButtons = _leftOrderItems
                .Select(item => item.Id)
                .ToList();
            settings.ToolbarHiddenButtons = _visibilityChecks
                .Where(check => check.IsChecked == false)
                .Select(check => check.Tag as string ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
        }

        private StackPanel CreateSection(EditorSettings settings, Func<string, string, string> getString)
        {
            var section = SettingsDialogUi.CreateSection();
            var topPanel = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_showLabelsCheck, 0);
            topPanel.Children.Add(_showLabelsCheck);
            section.Children.Add(topPanel);

            section.Children.Add(new TextBlock
            {
                Text = getString("SettingsToolbarButtonVisibility", "툴바 버튼 표시/숨기기"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 2)
            });

            AddVisibilityOptions(section, settings, getString);
            section.Children.Add(new TextBlock
            {
                Text = getString("SettingsToolbarSettingsPinned", "설정 버튼은 항상 표시됩니다."),
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                Margin = new Thickness(12, 0, 0, 4)
            });
            section.Children.Add(new TextBlock
            {
                Text = getString("SettingsToolbarDragHint", "왼쪽/오른쪽 목록에서 드래그하여 순서를 바꾸고, 선택한 버튼은 가운데 버튼으로 정렬 위치를 옮길 수 있습니다. 설정 버튼은 고정됩니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 4)
            });

            var leftOrderList = CreateToolbarOrderList(_leftOrderItems);
            var rightOrderList = CreateToolbarOrderList(_rightOrderItems);
            section.Children.Add(CreateAlignmentGrid(leftOrderList, rightOrderList, getString));

            var resetToolbarButton = new Button
            {
                Content = getString("SettingsToolbarReset", "툴바 초기화"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(resetToolbarButton, 1);
            topPanel.Children.Add(resetToolbarButton);
            resetToolbarButton.Click += (_, __) => ResetToolbar(getString);
            return section;
        }

        private void AddVisibilityOptions(StackPanel section, EditorSettings settings, Func<string, string, string> getString)
        {
            var hiddenSet = new HashSet<string>(
                (settings.ToolbarHiddenButtons ?? new List<string>())
                    .Select(ToolbarButtonCatalog.NormalizeId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var option in _toolbarOptions.Where(option => !option.IsRequired))
            {
                var checkBox = new CheckBox
                {
                    Content = getString(option.ResourceKey, option.Id),
                    Tag = option.Id,
                    IsChecked = !hiddenSet.Contains(option.Id),
                    Margin = new Thickness(12, 0, 0, 0)
                };
                _visibilityChecks.Add(checkBox);
                section.Children.Add(checkBox);
            }
        }

        private Grid CreateAlignmentGrid(
            ListView leftOrderList,
            ListView rightOrderList,
            Func<string, string, string> getString)
        {
            var alignmentGrid = new Grid();
            alignmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            alignmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            alignmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftColumn = new StackPanel { Spacing = 4 };
            leftColumn.Children.Add(new TextBlock
            {
                Text = getString("SettingsToolbarLeftAligned", "왼쪽 정렬"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 11
            });
            leftColumn.Children.Add(leftOrderList);

            var movePanel = new StackPanel
            {
                Spacing = 6,
                Width = 42,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 20, 6, 0)
            };
            var moveRightButton = new Button { Content = ">", HorizontalAlignment = HorizontalAlignment.Stretch };
            var moveLeftButton = new Button { Content = "<", HorizontalAlignment = HorizontalAlignment.Stretch };
            movePanel.Children.Add(moveRightButton);
            movePanel.Children.Add(moveLeftButton);

            var rightColumn = new StackPanel { Spacing = 4 };
            rightColumn.Children.Add(new TextBlock
            {
                Text = getString("SettingsToolbarRightAligned", "오른쪽 정렬"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 11
            });
            rightColumn.Children.Add(rightOrderList);

            moveRightButton.Click += (_, __) => MoveSelectedToolbarItem(leftOrderList, _leftOrderItems, rightOrderList, _rightOrderItems);
            moveLeftButton.Click += (_, __) => MoveSelectedToolbarItem(rightOrderList, _rightOrderItems, leftOrderList, _leftOrderItems);

            Grid.SetColumn(leftColumn, 0);
            Grid.SetColumn(movePanel, 1);
            Grid.SetColumn(rightColumn, 2);
            alignmentGrid.Children.Add(leftColumn);
            alignmentGrid.Children.Add(movePanel);
            alignmentGrid.Children.Add(rightColumn);
            return alignmentGrid;
        }

        private static ListView CreateToolbarOrderList(ObservableCollection<ToolbarOrderItem> items)
        {
            var list = new ListView
            {
                Height = 145,
                SelectionMode = ListViewSelectionMode.Single,
                AllowDrop = true,
                CanReorderItems = true,
                ItemsSource = items
            };

            list.ItemTemplate = (Microsoft.UI.Xaml.DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <TextBlock Text=""{Binding Label}"" FontSize=""11"" Height=""18"" VerticalAlignment=""Center""/>
                  </DataTemplate>");

            list.ItemContainerStyle = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<Style TargetType=""ListViewItem"" xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <Setter Property=""MinHeight"" Value=""22""/>
                    <Setter Property=""Height"" Value=""22""/>
                    <Setter Property=""Padding"" Value=""8,1,8,1""/>
                  </Style>");

            return list;
        }

        private static void MoveSelectedToolbarItem(
            ListView sourceList,
            ObservableCollection<ToolbarOrderItem> sourceItems,
            ListView targetList,
            ObservableCollection<ToolbarOrderItem> targetItems)
        {
            if (sourceList.SelectedItem is not ToolbarOrderItem item)
            {
                return;
            }

            int sourceIndex = sourceItems.IndexOf(item);
            if (sourceIndex < 0)
            {
                return;
            }

            sourceItems.RemoveAt(sourceIndex);
            targetItems.Add(item);
            targetList.SelectedItem = item;
        }

        private void ResetToolbar(Func<string, string, string> getString)
        {
            _showLabelsCheck.IsChecked = true;
            foreach (var checkBox in _visibilityChecks)
            {
                checkBox.IsChecked = true;
            }

            _leftOrderItems.Clear();
            _rightOrderItems.Clear();
            var defaultLeftSet = new HashSet<string>(ToolbarButtonCatalog.DefaultLeftAlignedButtons, StringComparer.OrdinalIgnoreCase);
            foreach (var id in NormalizeToolbarOrder(null))
            {
                var option = _toolbarOptions.First(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                var item = new ToolbarOrderItem(option.Id, getString(option.ResourceKey, option.Id));
                if (defaultLeftSet.Contains(option.Id))
                {
                    _leftOrderItems.Add(item);
                }
                else
                {
                    _rightOrderItems.Add(item);
                }
            }
        }

        private static List<string> NormalizeToolbarOrder(IReadOnlyList<string>? savedOrder)
        {
            var validIds = new HashSet<string>(
                ToolbarButtonCatalog.DefaultOrder,
                StringComparer.OrdinalIgnoreCase);
            var orderedIds = new List<string>();

            foreach (string rawId in savedOrder ?? Array.Empty<string>())
            {
                string id = ToolbarButtonCatalog.NormalizeId(rawId);
                if (validIds.Contains(id) && !orderedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    orderedIds.Add(id);
                }
            }

            foreach (string id in ToolbarButtonCatalog.DefaultOrder)
            {
                if (!orderedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    orderedIds.Add(id);
                }
            }

            return orderedIds;
        }

        private static List<string> NormalizeToolbarLeftAlignedButtons(IReadOnlyList<string>? savedLeftAlignedButtons)
        {
            var validIds = new HashSet<string>(
                ToolbarButtonCatalog.DefaultOrder,
                StringComparer.OrdinalIgnoreCase);
            var leftAlignedIds = new List<string>();

            foreach (string rawId in savedLeftAlignedButtons ?? ToolbarButtonCatalog.DefaultLeftAlignedButtons)
            {
                string id = ToolbarButtonCatalog.NormalizeId(rawId);
                if (validIds.Contains(id) && !leftAlignedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    leftAlignedIds.Add(id);
                }
            }

            return leftAlignedIds;
        }
    }
}
