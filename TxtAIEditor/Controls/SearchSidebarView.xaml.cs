using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace TxtAIEditor.Controls
{
    public sealed partial class SearchSidebarView : UserControl
    {
        private string _collapseAllText = string.Empty;
        private string _expandAllText = string.Empty;

        public SearchSidebarView()
        {
            InitializeComponent();
        }

        public Grid Root => RootGrid;
        public TextBlock Header => SearchHeaderText;
        public FrameworkElement ProgressIndicator => SearchProgressDot;
        public Button SearchAllButtonControl => SearchAllButton;
        public Button ReplaceAllButtonControl => ReplaceAllButton;
        public ListView Results => SearchResultsList;
        public TextBox SearchQuery => SearchQueryInput;
        public TextBox ReplaceQuery => ReplaceQueryInput;
        public ToggleButton MatchCase => SearchMatchCaseToggle;
        public ToggleButton WholeWord => SearchWholeWordToggle;
        public ToggleButton Regex => SearchRegexToggle;
        public ToggleButton IncludeHidden => SearchIncludeHiddenToggle;

        public event KeyEventHandler? QueryKeyDown;
        public event RoutedEventHandler? SearchAllClick;
        public event RoutedEventHandler? ReplaceAllClick;
        public event RoutedEventHandler? ClearResultsClick;
        public event RoutedEventHandler? ReplaceOneClick;
        public event EventHandler<string>? ResultFileClick;
        public event ItemClickEventHandler? ResultItemClick;

        public void Localize(Func<string, string, string> getString)
        {
            SearchHeaderText.Text = getString("SearchHeader", "파일 내용 검색 및 바꾸기");
            SearchQueryInput.PlaceholderText = getString("SearchPlaceholder", "검색어 입력...");
            ReplaceQueryInput.PlaceholderText = getString("ReplacePlaceholder", "바꿀 단어 입력...");
            ToolTipService.SetToolTip(SearchMatchCaseToggle, getString("SearchMatchCaseTooltip", "대소문자 구분"));
            ToolTipService.SetToolTip(SearchWholeWordToggle, getString("SearchWholeWordTooltip", "단어 단위"));
            ToolTipService.SetToolTip(SearchRegexToggle, getString("SearchRegexTooltip", "정규식 검색"));
            string includeHidden = getString("SearchIncludeHiddenTooltip", "숨김 파일 및 폴더 포함");
            ToolTipService.SetToolTip(SearchIncludeHiddenToggle, includeHidden);
            AutomationProperties.SetName(SearchIncludeHiddenToggle, includeHidden);
            SearchAllButton.Content = getString("SearchAllFiles", "검색");
            ReplaceAllButton.Content = getString("ReplaceAllFiles", "모두 바꾸기");
            ClearResultsButton.Content = getString("SearchClearResults", "지우기");
            ToolTipService.SetToolTip(ClearResultsButton, getString("SearchClearResultsTooltip", "검색을 중지하고 검색어와 결과 지우기"));
            _collapseAllText = getString("EditorContextMenuCollapseAll", "전체 접기");
            _expandAllText = getString("EditorContextMenuExpandAll", "전체 펼치기");
            UpdateSearchResultsButton();

            if (Resources.TryGetValue("LocBridge", out var bridgeObject) &&
                bridgeObject is LocalizationBridge bridge)
            {
                bridge.ReplaceOneTooltip = getString("SearchReplaceOneTooltip", "이 항목만 바꾸기");
            }
        }

        private void OnSearchQueryInputKeyDown(object sender, KeyRoutedEventArgs e) => QueryKeyDown?.Invoke(sender, e);
        private void OnSearchAllFilesClick(object sender, RoutedEventArgs e) => SearchAllClick?.Invoke(sender, e);
        private void OnReplaceAllClick(object sender, RoutedEventArgs e) => ReplaceAllClick?.Invoke(sender, e);
        private void OnClearResultsClick(object sender, RoutedEventArgs e) => ClearResultsClick?.Invoke(sender, e);
        private void OnReplaceOneClick(object sender, RoutedEventArgs e) => ReplaceOneClick?.Invoke(sender, e);

        private void OnToggleSearchResultsClick(object sender, RoutedEventArgs e)
        {
            bool collapse = SearchResultsList.Visibility == Visibility.Visible;
            SearchResultsList.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            SearchResultFilesList.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
            UpdateSearchResultsButton();
        }

        private void UpdateSearchResultsButton()
        {
            bool expanded = SearchResultsList.Visibility == Visibility.Visible;
            ToggleSearchResultsIcon.Glyph = expanded ? "\uE70E" : "\uE70D";
            string action = expanded ? _collapseAllText : _expandAllText;
            ToolTipService.SetToolTip(ToggleSearchResultsButton, action);
            AutomationProperties.SetName(ToggleSearchResultsButton, action);
        }

        private void OnSearchResultFileClick(object sender, RoutedEventArgs e)
        {
            if (sender is HyperlinkButton { Tag: string path })
            {
                ResultFileClick?.Invoke(this, path);
            }
        }

        private void OnSearchResultItemClick(object sender, ItemClickEventArgs e) => ResultItemClick?.Invoke(sender, e);
    }
}
