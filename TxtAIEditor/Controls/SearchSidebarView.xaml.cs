using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace TxtAIEditor.Controls
{
    public sealed partial class SearchSidebarView : UserControl
    {
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

        public event KeyEventHandler? QueryKeyDown;
        public event RoutedEventHandler? SearchAllClick;
        public event RoutedEventHandler? ReplaceAllClick;
        public event RoutedEventHandler? ReplaceOneClick;
        public event ItemClickEventHandler? ResultItemClick;

        public void Localize(Func<string, string, string> getString)
        {
            SearchHeaderText.Text = getString("SearchHeader", "폴더 전체 검색 및 바꾸기");
            SearchQueryInput.PlaceholderText = getString("SearchPlaceholder", "검색어 입력...");
            ReplaceQueryInput.PlaceholderText = getString("ReplacePlaceholder", "바꿀 단어 입력...");
            ToolTipService.SetToolTip(SearchMatchCaseToggle, getString("SearchMatchCaseTooltip", "대소문자 구분"));
            ToolTipService.SetToolTip(SearchWholeWordToggle, getString("SearchWholeWordTooltip", "단어 단위"));
            ToolTipService.SetToolTip(SearchRegexToggle, getString("SearchRegexTooltip", "정규식 검색"));
            SearchAllButton.Content = getString("SearchAllFiles", "전체 검색");
            ReplaceAllButton.Content = getString("ReplaceAllFiles", "모두 바꾸기");

            if (Resources.TryGetValue("LocBridge", out var bridgeObject) &&
                bridgeObject is LocalizationBridge bridge)
            {
                bridge.ReplaceOneTooltip = getString("SearchReplaceOneTooltip", "이 항목만 바꾸기");
            }
        }

        private void OnSearchQueryInputKeyDown(object sender, KeyRoutedEventArgs e) => QueryKeyDown?.Invoke(sender, e);
        private void OnSearchAllFilesClick(object sender, RoutedEventArgs e) => SearchAllClick?.Invoke(sender, e);
        private void OnReplaceAllClick(object sender, RoutedEventArgs e) => ReplaceAllClick?.Invoke(sender, e);
        private void OnReplaceOneClick(object sender, RoutedEventArgs e) => ReplaceOneClick?.Invoke(sender, e);
        private void OnSearchResultItemClick(object sender, ItemClickEventArgs e) => ResultItemClick?.Invoke(sender, e);
    }
}
