using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    public sealed partial class TopCommandBarPane : UserControl
    {
        public TopCommandBarPane()
        {
            InitializeComponent();
        }

        public event RoutedEventHandler? OpenFileClick;
        public event RoutedEventHandler? SaveFileClick;
        public event RoutedEventHandler? SaveAsFileClick;
        public event RoutedEventHandler? CompareFilesClick;
        public event RoutedEventHandler? OpenTerminalClick;
        public event RoutedEventHandler? PrintClick;
        public event RoutedEventHandler? TopMostToggleClick;
        public event RoutedEventHandler? StickyNoteClick;
        public event RoutedEventHandler? WordWrapToggleClick;
        public event RoutedEventHandler? FindClick;
        public event RoutedEventHandler? ToggleMarkdownToolbarClick;
        public event RoutedEventHandler? ToggleCsvTableClick;
        public event RoutedEventHandler? ToggleThemeClick;
        public event RoutedEventHandler? SplitNoneClick;
        public event RoutedEventHandler? SplitVerticalClick;
        public event RoutedEventHandler? SplitHorizontalClick;
        public event RoutedEventHandler? SettingsClick;

        public bool WordWrapIsChecked
        {
            get => WordWrapToggle.IsChecked == true;
            set => WordWrapToggle.IsChecked = value;
        }

        public bool MarkdownToolbarIsChecked
        {
            get => MarkdownToolbarToggle.IsChecked == true;
            set => MarkdownToolbarToggle.IsChecked = value;
        }

        public bool CsvTableIsChecked
        {
            get => CsvTableToggle.IsChecked == true;
            set => CsvTableToggle.IsChecked = value;
        }

        public bool TerminalIsChecked
        {
            get => TerminalToggleButton.IsChecked == true;
            set => TerminalToggleButton.IsChecked = value;
        }

        public bool TopMostIsChecked
        {
            get => TopMostToggleButton.IsChecked == true;
            set => TopMostToggleButton.IsChecked = value;
        }

        public void Localize(Func<string, string, string> getString)
        {
            OpenFileButton.Label = getString("OpenFile", "파일 열기");
            ToolTipService.SetToolTip(OpenFileButton, getString("OpenFile", "파일 열기") + " (Ctrl+O)");

            SaveFileButton.Label = getString("SaveFile", "저장");
            ToolTipService.SetToolTip(SaveFileButton, getString("SaveFile", "저장") + " (Ctrl+S)");

            SaveAsFileButton.Label = getString("SaveAsFile", "다른 이름으로 저장");
            ToolTipService.SetToolTip(SaveAsFileButton, getString("SaveAsFile", "다른 이름으로 저장") + " (Ctrl+Shift+S)");

            CompareButton.Label = getString("Compare", "비교");
            ToolTipService.SetToolTip(CompareButton, getString("Compare", "비교") + " (Diff)");

            TerminalToggleButton.Label = getString("Terminal", "터미널");
            ToolTipService.SetToolTip(TerminalToggleButton, getString("Terminal", "터미널") + " (Ctrl+`)");

            TopMostToggleButton.Label = getString("TopMost", "항상위");
            ToolTipService.SetToolTip(TopMostToggleButton, getString("TopMost", "항상 위") + " (F9)");

            StickyNoteButton.Label = getString("StickyNote", "스티커");
            ToolTipService.SetToolTip(StickyNoteButton, getString("StickyNote", "스티커 노트") + " (F12)");

            WordWrapToggle.Label = getString("WordWrap", "Word Wrap");

            SearchButton.Label = getString("Search", "검색");
            ToolTipService.SetToolTip(SearchButton, getString("Search", "검색") + " (Ctrl+F)");

            MarkdownToolbarToggle.Label = getString("Markdown", "Markdown");
            ToolTipService.SetToolTip(MarkdownToolbarToggle, getString("Markdown", "마크다운 툴바 토글"));

            CsvTableToggle.Label = getString("CsvTable", "CSV 테이블");
            ToolTipService.SetToolTip(CsvTableToggle, getString("CsvTable", "CSV 테이블"));

            ThemeButton.Label = getString("Theme", "테마");
            ToolTipService.SetToolTip(ThemeButton, getString("Theme", "테마") + " (F10)");

            SplitButton.Label = getString("Split", "분할");
            ToolTipService.SetToolTip(SplitButton, getString("Split", "에디터 화면 분할"));

            SettingsButton.Label = getString("Settings", "설정");

            PrintButton.Label = getString("Print", "인쇄");
            ToolTipService.SetToolTip(PrintButton, getString("Print", "인쇄") + " (Ctrl+P)");

            SplitNoneItem.Text = getString("SplitNone", "분할 없음 (단일)");
            SplitVerticalItem.Text = getString("SplitVertical", "좌우 분할");
            SplitHorizontalItem.Text = getString("SplitHorizontal", "상하 분할");
        }

        public void ApplySettings(EditorSettings settings, Func<string, string, string> getString)
        {
            bool showLabels = settings.ToolbarShowLabels;
            TopCommandBar.DefaultLabelPosition = showLabels
                ? CommandBarDefaultLabelPosition.Right
                : CommandBarDefaultLabelPosition.Collapsed;

            var hiddenSet = new HashSet<string>(
                (settings.ToolbarHiddenButtons ?? new List<string>()).Select(ToolbarButtonCatalog.NormalizeId),
                StringComparer.OrdinalIgnoreCase);
            var leftAlignedSet = new HashSet<string>(
                NormalizeToolbarLeftAlignedButtons(settings.ToolbarLeftAlignedButtons),
                StringComparer.OrdinalIgnoreCase);
            var buttonsById = GetToolbarButtonsById();
            var orderedIds = NormalizeToolbarOrder(settings.ToolbarButtonOrder);

            LeftFileCommandPanel.Children.Clear();
            TopCommandBar.PrimaryCommands.Clear();
            AddToolbarCommandsToLeftPanel(
                orderedIds.Where(id => leftAlignedSet.Contains(id)).ToList(),
                buttonsById,
                hiddenSet);
            AddToolbarCommandsInOriginalGroups(
                orderedIds.Where(id => !leftAlignedSet.Contains(id)).ToList(),
                buttonsById,
                hiddenSet,
                showLabels);

            foreach (var (id, entry) in buttonsById)
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

            const double compactToolbarButtonWidth = 40;
            button.Width = compactToolbarButtonWidth;
            button.MinWidth = compactToolbarButtonWidth;
            button.MaxWidth = compactToolbarButtonWidth;
        }

        private void AddToolbarCommandsToLeftPanel(
            IReadOnlyList<string> orderedIds,
            IReadOnlyDictionary<string, (FrameworkElement Button, string ResourceKey)> buttonsById,
            ISet<string> hiddenSet)
        {
            var groupLookup = ToolbarButtonCatalog.DefaultGroups
                .SelectMany((group, groupIndex) => group.Select(id => (id, groupIndex)))
                .ToDictionary(item => item.id, item => item.groupIndex, StringComparer.OrdinalIgnoreCase);
            int? lastGroupIndex = null;

            foreach (string id in orderedIds)
            {
                if (!buttonsById.TryGetValue(id, out var entry) || hiddenSet.Contains(id))
                {
                    continue;
                }

                int groupIndex = groupLookup.TryGetValue(id, out int index) ? index : 0;
                entry.Button.Margin = lastGroupIndex.HasValue && groupIndex != lastGroupIndex.Value
                    ? new Thickness(8, 0, 0, 0)
                    : new Thickness(0);

                LeftFileCommandPanel.Children.Add(entry.Button);
                lastGroupIndex = groupIndex;
            }
        }

        private void AddToolbarCommandsInOriginalGroups(
            IReadOnlyList<string> orderedIds,
            IReadOnlyDictionary<string, (FrameworkElement Button, string ResourceKey)> buttonsById,
            ISet<string> hiddenSet,
            bool showSeparators)
        {
            var groupLookup = ToolbarButtonCatalog.DefaultGroups
                .SelectMany((group, groupIndex) => group.Select(id => (id, groupIndex)))
                .ToDictionary(item => item.id, item => item.groupIndex, StringComparer.OrdinalIgnoreCase);
            int? lastGroupIndex = null;

            foreach (string id in orderedIds.Concat(new[] { "settings" }))
            {
                if (!buttonsById.TryGetValue(id, out var entry))
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
                        TopCommandBar.PrimaryCommands.Add(new AppBarSeparator());
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

                TopCommandBar.PrimaryCommands.Add((ICommandBarElement)entry.Button);
                lastGroupIndex = groupIndex;
            }
        }

        private Dictionary<string, (FrameworkElement Button, string ResourceKey)> GetToolbarButtonsById()
        {
            return new Dictionary<string, (FrameworkElement Button, string ResourceKey)>(StringComparer.OrdinalIgnoreCase)
            {
                ["openFile"] = (OpenFileButton, "OpenFile"),
                ["saveFile"] = (SaveFileButton, "SaveFile"),
                ["saveAsFile"] = (SaveAsFileButton, "SaveAsFile"),
                ["compare"] = (CompareButton, "Compare"),
                ["terminal"] = (TerminalToggleButton, "Terminal"),
                ["print"] = (PrintButton, "Print"),
                ["topMost"] = (TopMostToggleButton, "TopMost"),
                ["stickyNote"] = (StickyNoteButton, "StickyNote"),
                ["wordWrap"] = (WordWrapToggle, "WordWrap"),
                ["search"] = (SearchButton, "Search"),
                ["markdown"] = (MarkdownToolbarToggle, "Markdown"),
                ["csvTable"] = (CsvTableToggle, "CsvTable"),
                ["theme"] = (ThemeButton, "Theme"),
                ["split"] = (SplitButton, "Split"),
                ["settings"] = (SettingsButton, "Settings")
            };
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

        private void OnOpenFileClick(object sender, RoutedEventArgs e) => OpenFileClick?.Invoke(sender, e);
        private void OnSaveFileClick(object sender, RoutedEventArgs e) => SaveFileClick?.Invoke(sender, e);
        private void OnSaveAsFileClick(object sender, RoutedEventArgs e) => SaveAsFileClick?.Invoke(sender, e);
        private void OnCompareFilesClick(object sender, RoutedEventArgs e) => CompareFilesClick?.Invoke(sender, e);
        private void OnOpenTerminalClick(object sender, RoutedEventArgs e) => OpenTerminalClick?.Invoke(sender, e);
        private void OnPrintClick(object sender, RoutedEventArgs e) => PrintClick?.Invoke(sender, e);
        private void OnTopMostToggleClick(object sender, RoutedEventArgs e) => TopMostToggleClick?.Invoke(sender, e);
        private void OnStickyNoteClick(object sender, RoutedEventArgs e) => StickyNoteClick?.Invoke(sender, e);
        private void OnWordWrapToggleClick(object sender, RoutedEventArgs e) => WordWrapToggleClick?.Invoke(sender, e);
        private void OnFindClick(object sender, RoutedEventArgs e) => FindClick?.Invoke(sender, e);
        private void OnToggleMarkdownToolbarClick(object sender, RoutedEventArgs e) => ToggleMarkdownToolbarClick?.Invoke(sender, e);
        private void OnToggleCsvTableClick(object sender, RoutedEventArgs e) => ToggleCsvTableClick?.Invoke(sender, e);
        private void OnToggleThemeClick(object sender, RoutedEventArgs e) => ToggleThemeClick?.Invoke(sender, e);
        private void OnSplitNoneClick(object sender, RoutedEventArgs e) => SplitNoneClick?.Invoke(sender, e);
        private void OnSplitVerticalClick(object sender, RoutedEventArgs e) => SplitVerticalClick?.Invoke(sender, e);
        private void OnSplitHorizontalClick(object sender, RoutedEventArgs e) => SplitHorizontalClick?.Invoke(sender, e);
        private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsClick?.Invoke(sender, e);
    }
}
