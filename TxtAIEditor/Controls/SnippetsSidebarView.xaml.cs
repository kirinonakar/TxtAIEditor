using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace TxtAIEditor.Controls
{
    public sealed partial class SnippetsSidebarView : UserControl
    {
        public SnippetsSidebarView()
        {
            InitializeComponent();
        }

        public Grid Root => RootGrid;
        public TextBlock Header => SnippetsHeaderText;
        public ListView Items => SnippetsListView;
        public Button AddButton => AddSnippetButton;
        public Button ExportButton => ExportSnippetsButton;
        public Button ImportButton => ImportSnippetsButton;
        public Button ResetButton => ResetSnippetsButton;
        public Button AutocompleteDictionaryButton => AutocompleteDictButton;

        public event DoubleTappedEventHandler? ItemDoubleTapped;
        public event RoutedEventHandler? DeleteClick;
        public event RoutedEventHandler? EditClick;
        public event RoutedEventHandler? AddClick;
        public event RoutedEventHandler? ExportClick;
        public event RoutedEventHandler? ImportClick;
        public event RoutedEventHandler? ResetClick;
        public event RoutedEventHandler? AutocompleteDictionaryClick;

        public void Localize(Func<string, string, string> getString)
        {
            SnippetsHeaderText.Text = getString("SnippetsHeader", "코드 및 수식 템플릿");
            AddSnippetButton.Content = getString("AddSnippet", "스니펫 추가...");
            ExportSnippetsButton.Content = getString("SnippetExport", "내보내기");
            ImportSnippetsButton.Content = getString("SnippetImport", "가져오기");
            ResetSnippetsButton.Content = getString("SnippetReset", "초기화");
            AutocompleteDictButton.Content = getString("AutocompleteDict", "자동완성 사전...");

            if (Resources.TryGetValue("LocBridge", out var bridgeObject) &&
                bridgeObject is LocalizationBridge bridge)
            {
                bridge.SnippetEditTooltip = getString("SnippetEditTooltip", "수정");
                bridge.SnippetDeleteTooltip = getString("SnippetDeleteTooltip", "삭제");
            }
        }

        private void OnSnippetItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ItemDoubleTapped?.Invoke(sender, e);
        private void OnDeleteSnippetClick(object sender, RoutedEventArgs e) => DeleteClick?.Invoke(sender, e);
        private void OnEditSnippetClick(object sender, RoutedEventArgs e) => EditClick?.Invoke(sender, e);
        private void OnAddSnippetClick(object sender, RoutedEventArgs e) => AddClick?.Invoke(sender, e);
        private void OnExportSnippetsClick(object sender, RoutedEventArgs e) => ExportClick?.Invoke(sender, e);
        private void OnImportSnippetsClick(object sender, RoutedEventArgs e) => ImportClick?.Invoke(sender, e);
        private void OnResetSnippetsClick(object sender, RoutedEventArgs e) => ResetClick?.Invoke(sender, e);
        private void OnAutocompleteDictClick(object sender, RoutedEventArgs e) => AutocompleteDictionaryClick?.Invoke(sender, e);
    }
}
