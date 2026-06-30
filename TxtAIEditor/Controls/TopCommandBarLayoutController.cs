using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal sealed class TopCommandBarLayoutController
    {
        private const double CompactToolbarButtonWidth = 40;

        private readonly CommandBar _topCommandBar;
        private readonly StackPanel _leftFileCommandPanel;
        private readonly IReadOnlyDictionary<string, (FrameworkElement Button, string ResourceKey)> _buttonsById;

        public TopCommandBarLayoutController(
            CommandBar topCommandBar,
            StackPanel leftFileCommandPanel,
            IReadOnlyDictionary<string, (FrameworkElement Button, string ResourceKey)> buttonsById)
        {
            _topCommandBar = topCommandBar;
            _leftFileCommandPanel = leftFileCommandPanel;
            _buttonsById = buttonsById;
        }

        public void ApplySettings(EditorSettings settings, Func<string, string, string> getString)
        {
            bool showLabels = settings.ToolbarShowLabels;
            _topCommandBar.DefaultLabelPosition = showLabels
                ? CommandBarDefaultLabelPosition.Right
                : CommandBarDefaultLabelPosition.Collapsed;

            var hiddenSet = new HashSet<string>(
                (settings.ToolbarHiddenButtons ?? new List<string>()).Select(ToolbarButtonCatalog.NormalizeId),
                StringComparer.OrdinalIgnoreCase);
            var leftAlignedSet = new HashSet<string>(
                NormalizeToolbarLeftAlignedButtons(settings.ToolbarLeftAlignedButtons),
                StringComparer.OrdinalIgnoreCase);
            var orderedIds = NormalizeToolbarOrder(settings.ToolbarButtonOrder);

            _leftFileCommandPanel.Children.Clear();
            _topCommandBar.PrimaryCommands.Clear();
            AddToolbarCommandsToLeftPanel(
                orderedIds.Where(id => leftAlignedSet.Contains(id)).ToList(),
                hiddenSet);
            AddToolbarCommandsInOriginalGroups(
                orderedIds.Where(id => !leftAlignedSet.Contains(id)).ToList(),
                hiddenSet,
                showLabels);

            foreach (var (id, entry) in _buttonsById)
            {
                bool isSettings = id.Equals("settings", StringComparison.OrdinalIgnoreCase);
                entry.Button.Visibility = isSettings || !hiddenSet.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
                string label = getString(entry.ResourceKey, id);
                if (entry.Button is AppBarButton appBarButton)
                {
                    appBarButton.Label = label;
                    ApplyToolbarButtonPresentation(appBarButton, showLabels && !leftAlignedSet.Contains(id));
                }
                else if (entry.Button is AppBarToggleButton appBarToggleButton)
                {
                    appBarToggleButton.Label = label;
                    ApplyToolbarButtonPresentation(appBarToggleButton, showLabels && !leftAlignedSet.Contains(id));
                }
            }
        }

        private static void ApplyToolbarButtonPresentation(Control button, bool showLabels)
        {
            if (button is AppBarButton appBarButton)
            {
                appBarButton.LabelPosition = showLabels
                    ? CommandBarLabelPosition.Default
                    : CommandBarLabelPosition.Collapsed;
            }
            else if (button is AppBarToggleButton appBarToggleButton)
            {
                appBarToggleButton.LabelPosition = showLabels
                    ? CommandBarLabelPosition.Default
                    : CommandBarLabelPosition.Collapsed;
            }

            if (showLabels)
            {
                button.Width = double.NaN;
                button.MinWidth = 0;
                button.MaxWidth = double.PositiveInfinity;
                return;
            }

            button.Width = CompactToolbarButtonWidth;
            button.MinWidth = CompactToolbarButtonWidth;
            button.MaxWidth = CompactToolbarButtonWidth;
        }

        private void AddToolbarCommandsToLeftPanel(
            IReadOnlyList<string> orderedIds,
            ISet<string> hiddenSet)
        {
            var groupLookup = GetGroupLookup();
            int? lastGroupIndex = null;

            foreach (string id in orderedIds)
            {
                if (!_buttonsById.TryGetValue(id, out var entry) || hiddenSet.Contains(id))
                {
                    continue;
                }

                int groupIndex = groupLookup.TryGetValue(id, out int index) ? index : 0;
                entry.Button.Margin = lastGroupIndex.HasValue && groupIndex != lastGroupIndex.Value
                    ? new Thickness(8, 0, 0, 0)
                    : new Thickness(0);

                _leftFileCommandPanel.Children.Add(entry.Button);
                lastGroupIndex = groupIndex;
            }
        }

        private void AddToolbarCommandsInOriginalGroups(
            IReadOnlyList<string> orderedIds,
            ISet<string> hiddenSet,
            bool showSeparators)
        {
            var groupLookup = GetGroupLookup();
            int? lastGroupIndex = null;

            foreach (string id in orderedIds.Concat(new[] { "settings" }))
            {
                if (!_buttonsById.TryGetValue(id, out var entry))
                {
                    continue;
                }

                bool isSettings = id.Equals("settings", StringComparison.OrdinalIgnoreCase);
                if (!isSettings && hiddenSet.Contains(id))
                {
                    continue;
                }

                int groupIndex = groupLookup.TryGetValue(id, out int index) ? index : 0;
                if (lastGroupIndex.HasValue && groupIndex != lastGroupIndex.Value)
                {
                    if (showSeparators)
                    {
                        _topCommandBar.PrimaryCommands.Add(new AppBarSeparator());
                        entry.Button.Margin = new Thickness(0);
                    }
                    else
                    {
                        entry.Button.Margin = new Thickness(8, 0, 0, 0);
                    }
                }
                else
                {
                    entry.Button.Margin = new Thickness(0);
                }

                _topCommandBar.PrimaryCommands.Add((ICommandBarElement)entry.Button);
                lastGroupIndex = groupIndex;
            }
        }

        private static Dictionary<string, int> GetGroupLookup()
        {
            return ToolbarButtonCatalog.DefaultGroups
                .SelectMany((group, groupIndex) => group.Select(id => (id, groupIndex)))
                .ToDictionary(item => item.id, item => item.groupIndex, StringComparer.OrdinalIgnoreCase);
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
