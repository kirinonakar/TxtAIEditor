using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Controls
{
    public sealed partial class RightSidebarPane : UserControl
    {
        public RightSidebarPane()
        {
            InitializeComponent();
        }

        public event SelectionChangedEventHandler? PreviewModeSelectionChanged;
        public event RoutedEventHandler? OpenPreviewInBrowserClick;
        public event RoutedEventHandler? LlmAddFileContextClick;
        public event RoutedEventHandler? LlmRemoveFileContextClick;
        public event RoutedEventHandler? LlmExplainClick;
        public event RoutedEventHandler? LlmSummarizeClick;
        public event RoutedEventHandler? LlmTranslateClick;
        public event RoutedEventHandler? LlmImproveClick;
        public event Action<string>? LlmTargetLanguageSelected;
        private Func<string, string, string>? _getString;
        private string _currentTargetLanguage = "Korean";
        public event RoutedEventHandler? LlmCustomClick;
        public event RoutedEventHandler? LlmInsertOutputClick;
        public event RoutedEventHandler? LlmInsertNewTabOutputClick;
        public event RoutedEventHandler? LlmAddInstructionClick;

        public TabView RightTabs => RightTabView;
        public ComboBox PreviewMode => PreviewModeCombo;
        public WebView2 PreviewWebViewControl => PreviewWebView;
        public TextBox LlmOutput => LlmOutputText;
        public TextBlock SelectionStats => SelectionStatsText;
        public TextBox LlmFileContext => LlmFileContextInput;
        public TextBox LlmCustomPrompt => LlmCustomPromptInput;
        public AgentPane AgentPane => AgentPaneControl;

        // Named controls exposed for localization
        public TabViewItem LivePreviewTabItem => LivePreviewTab;
        public ComboBoxItem ComboMarkdown => ComboItemMarkdown;
        public ComboBoxItem ComboHtml => ComboItemHtml;
        public ComboBoxItem ComboLatex => ComboItemLatex;
        public ComboBoxItem ComboAozora => ComboItemAozora;
        public ComboBoxItem ComboCsv => ComboItemCsv;
        public Button OpenBrowserBtn => OpenBrowserButton;
        public TextBlock OpenBrowserBtnText => OpenBrowserButtonText;
        public TabViewItem AiAssistantTabItem => AiAssistantTab;
        public TabViewItem AgentTabItem => AgentTab;
        public Button LlmAddFileCtxButton => LlmAddFileContextButton;
        public Button LlmRemoveFileCtxButton => LlmRemoveFileContextButton;
        public Button LlmPresetBtn => LlmPresetButton;
        public Button LlmExplainBtn => LlmExplainButton;
        public Button LlmSummarizeBtn => LlmSummarizeButton;
        public Button LlmTranslateBtn => LlmTranslateButton;
        public Button LlmImproveBtn => LlmImproveButton;
        public Button LlmCustomRunBtn => LlmCustomRunButton;
        public Button LlmInsertOutputBtn => LlmInsertOutputButton;
        public Button LlmInsertNewTabOutputBtn => LlmInsertNewTabOutputButton;
        public Button LlmAddInstructionBtn => LlmAddInstructionButton;
        public ScrollViewer InstructionTabScroller => InstructionTabScrollViewer;

        public void UpdateLlmModelName(string text)
        {
            LlmModelNameText.Text = text;
        }

        public void Localize(Func<string, string, string> getString)
        {
            LivePreviewTab.Header = getString("LivePreviewTabHeader", "실시간 프리뷰");
            ComboItemMarkdown.Content = getString("ComboItemMarkdown", "Markdown");
            ComboItemHtml.Content = getString("ComboItemHtml", "HTML Preview");
            ComboItemLatex.Content = getString("ComboItemLatex", "LaTeX Block");
            ComboItemAozora.Content = getString("ComboItemAozora", "Aozora");
            ComboItemCsv.Content = getString("ComboItemCsv", "CSV Table");
            OpenBrowserButtonText.Text = getString("OpenInBrowserButtonText", "브라우저");
            ToolTipService.SetToolTip(OpenBrowserButton, getString("OpenInBrowserTooltip", "HTML 미리보기를 브라우저로 열기"));

            AiAssistantTab.Header = getString("AiAssistantTabHeader", "AI Assistant");
            AgentTab.Header = getString("AgentTabHeader", "Agent");
            AgentPaneControl.Localize(getString);

            string currentLlmText = LlmOutputText.Text;
            if (currentLlmText.Contains("대기 중...") || currentLlmText.Contains("待機中...") || currentLlmText.Contains("Waiting..."))
            {
                LlmOutputText.Text = getString("LlmOutputPlaceholder", "대기 중... 에디터에서 영역을 선택한 후 하단의 AI 분석 도구를 사용해 보세요.");
            }

            string currentStatsText = SelectionStatsText.Text;
            if (currentStatsText.Contains("선택 영역: 없음") || currentStatsText.Contains("選択範囲: なし") || currentStatsText.Contains("Selection: None"))
            {
                SelectionStatsText.Text = getString("SelectionStatsPlaceholder", "선택 영역: 없음 (전체 파일의 경우 파일 추가 사용)");
            }

            LlmFileContextInput.PlaceholderText = getString("LlmFileContextPlaceholder", "파일 맥락 없음");
            LlmAddFileContextButton.Content = getString("LlmAddFileContextButtonText", "파일 맥락 추가");
            LlmRemoveFileContextButton.Content = getString("LlmRemoveFileContextButtonText", "삭제");
            LlmExplainButton.Content = getString("LlmExplainButtonText", "설명");
            LlmSummarizeButton.Content = getString("LlmSummarizeButtonText", "요약");
            _getString = getString;
            UpdateTranslateButtonText();
            LlmTargetLangKorean.Text = getString("LlmLangKorean", "한국어 (Korean)");
            LlmTargetLangEnglish.Text = getString("LlmLangEnglish", "영어 (English)");
            LlmTargetLangJapanese.Text = getString("LlmLangJapanese", "일본어 (Japanese)");
            LlmTargetLangChinese.Text = getString("LlmLangChinese", "중국어 (Chinese)");
            LlmTargetLangFrench.Text = getString("LlmLangFrench", "프랑스어 (French)");
            LlmTargetLangSpanish.Text = getString("LlmLangSpanish", "스페인어 (Spanish)");
            LlmTargetLangGerman.Text = getString("LlmLangGerman", "독일어 (German)");
            LlmImproveButton.Content = getString("LlmImproveButtonText", "개선");
            LlmCustomPromptInput.PlaceholderText = getString("LlmCustomPromptPlaceholder", "질문이나 커스텀 지시사항 입력...");
            LlmCustomRunButton.Content = getString("LlmCustomRunButtonText", "전송");
            LlmInsertOutputButton.Content = getString("LlmInsertOutputButtonText", "입력");
            ToolTipService.SetToolTip(LlmInsertOutputButton, getString("LlmInsertOutputTooltip", "AI 응답을 현재 커서에 입력 (선택한 경우 선택부위만)"));
            LlmInsertNewTabOutputButton.Content = getString("LlmInsertNewTabOutputButtonText", "새 탭에 입력");
            ToolTipService.SetToolTip(LlmInsertNewTabOutputButton, getString("LlmInsertNewTabOutputTooltip", "AI 응답을 새 탭에 입력 (선택한 경우 선택부위만)"));
            ToolTipService.SetToolTip(LlmAddInstructionButton, getString("LlmAddInstructionTooltip", "새 커스텀 지시문 추가"));

            LlmPresetButton.Content = getString("LlmPresetButtonText", "프리셋");
            ToolTipService.SetToolTip(LlmPresetButton, getString("LlmPresetButtonTooltip", "지시사항 프리셋 목록"));
            if (LlmAddPresetText != null)
            {
                LlmAddPresetText.Text = getString("LlmPresetAddText", "프리셋 추가");
            }
            if (LlmExportPresetText != null)
            {
                LlmExportPresetText.Text = getString("PresetExportText", "내보내기");
            }
            if (LlmImportPresetText != null)
            {
                LlmImportPresetText.Text = getString("PresetImportText", "가져오기");
            }
            _getString = getString;
            RebuildPresetsMenu();
        }

        private void OnPreviewModeComboSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PreviewModeSelectionChanged?.Invoke(sender, e);
        }

        private void OnOpenPreviewInBrowserClick(object sender, RoutedEventArgs e)
        {
            OpenPreviewInBrowserClick?.Invoke(sender, e);
        }

        private void OnLlmAddFileContextClick(object sender, RoutedEventArgs e)
        {
            LlmAddFileContextClick?.Invoke(sender, e);
        }

        private void OnLlmRemoveFileContextClick(object sender, RoutedEventArgs e)
        {
            LlmRemoveFileContextClick?.Invoke(sender, e);
        }

        private void OnLlmExplainClick(object sender, RoutedEventArgs e)
        {
            LlmExplainClick?.Invoke(sender, e);
        }

        private void OnLlmSummarizeClick(object sender, RoutedEventArgs e)
        {
            LlmSummarizeClick?.Invoke(sender, e);
        }

        private void OnLlmTranslateClick(object sender, RoutedEventArgs e)
        {
            LlmTranslateClick?.Invoke(sender, e);
        }

        private void OnLlmImproveClick(object sender, RoutedEventArgs e)
        {
            LlmImproveClick?.Invoke(sender, e);
        }

        private void OnLlmCustomClick(object sender, RoutedEventArgs e)
        {
            LlmCustomClick?.Invoke(sender, e);
        }

        private void OnLlmCustomPromptInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
                if (ctrl)
                {
                    e.Handled = true;
                    LlmCustomClick?.Invoke(sender, e);
                }
            }
        }

        private void OnLlmInsertOutputClick(object sender, RoutedEventArgs e)
        {
            LlmInsertOutputClick?.Invoke(sender, e);
        }

        private void OnLlmInsertNewTabOutputClick(object sender, RoutedEventArgs e)
        {
            LlmInsertNewTabOutputClick?.Invoke(sender, e);
        }

        private void OnLlmAddInstructionClick(object sender, RoutedEventArgs e)
        {
            LlmAddInstructionClick?.Invoke(sender, e);
        }

        private void OnLlmTargetLangClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string lang)
            {
                LlmTargetLanguageSelected?.Invoke(lang);
            }
        }

        public void UpdateTranslateLanguage(string targetLanguage)
        {
            _currentTargetLanguage = targetLanguage;
            UpdateTranslateButtonText();
        }

        private void UpdateTranslateButtonText()
        {
            if (_getString == null) return;

            string baseText = _getString("LlmTranslateButtonText", "번역");
            
            string shortCode = _currentTargetLanguage switch
            {
                "Korean" => "KO",
                "English" => "EN",
                "Japanese" => "JP",
                "Chinese" => "ZH",
                "French" => "FR",
                "Spanish" => "ES",
                "German" => "DE",
                _ => "KO"
            };

            LlmTranslateButton.Content = $"{baseText} ({shortCode})";
        }

        public void UpdateInstructionTabs(IReadOnlyList<(string Name, bool IsActive)> tabs, Action<int> onTabClick, Action<int> onTabDelete)
        {
            InstructionTabPanel.Children.Clear();
            for (int i = 0; i < tabs.Count; i++)
            {
                var (name, isActive) = tabs[i];
                int index = i;

                var innerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

                var nameBlock = new TextBlock
                {
                    Text = name,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                innerStack.Children.Add(nameBlock);

                var closeBtn = new Button
                {
                    Content = "×",
                    FontSize = 10,
                    Width = 18,
                    Height = 18,
                    MinHeight = 18,
                    MinWidth = 18,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                closeBtn.Click += (s, args) => onTabDelete(index);
                closeBtn.Tapped += (s, args) => args.Handled = true;
                innerStack.Children.Add(closeBtn);

                var tabBorder = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 0, 4, 0),
                    BorderThickness = new Thickness(1),
                    MinHeight = 24,
                    Height = 24,
                    VerticalAlignment = VerticalAlignment.Center,
                    UseLayoutRounding = true,
                    Child = innerStack,
                };

                ApplyTabTheme(tabBorder, nameBlock, isActive);

                tabBorder.ActualThemeChanged += (_, _) => ApplyTabTheme(tabBorder, nameBlock, isActive);
                tabBorder.Tapped += (_, _) => onTabClick(index);

                InstructionTabPanel.Children.Add(tabBorder);
            }
        }

        private static void ApplyTabTheme(Border tabBorder, TextBlock nameBlock, bool isActive)
        {
            if (isActive)
            {
                tabBorder.Background = (Brush)Application.Current.Resources["AccentButtonBackground"];
                tabBorder.BorderBrush = (Brush)Application.Current.Resources["AccentButtonBackground"];
                nameBlock.Foreground = (Brush)Application.Current.Resources["SystemControlForegroundChromeWhiteBrush"];
            }
            else
            {
                tabBorder.Background = (Brush)Application.Current.Resources["ButtonBackground"];
                tabBorder.BorderBrush = (Brush)Application.Current.Resources["ButtonBorderBrush"];
                nameBlock.Foreground = (Brush)Application.Current.Resources["SystemControlForegroundBaseHighBrush"];
            }

            // Update close button foregrounds in children
            foreach (var child in ((StackPanel)tabBorder.Child).Children)
            {
                if (child is Button btn && btn.Content is string s && s == "×")
                {
                    btn.Foreground = isActive
                        ? (Brush)Application.Current.Resources["SystemControlForegroundChromeWhiteBrush"]
                        : (Brush)Application.Current.Resources["SystemControlForegroundBaseMediumBrush"];
                }
            }
        }

        private List<string> _presetNames = new();
        private Action? _onAddPresetClick;
        private Action<string>? _onPresetSelected;
        private Action<string>? _onPresetEdited;
        private Action<string>? _onPresetDeleted;
        private Action? _onExportPresetClick;
        private Action? _onImportPresetClick;

        public void UpdatePresetsMenu(
            List<string> presetNames,
            Action onAddPresetClick,
            Action<string> onPresetSelected,
            Action<string> onPresetEdited,
            Action<string> onPresetDeleted,
            Action onExportPresetClick,
            Action onImportPresetClick,
            Func<string, string, string> getString)
        {
            _presetNames = presetNames;
            _onAddPresetClick = onAddPresetClick;
            _onPresetSelected = onPresetSelected;
            _onPresetEdited = onPresetEdited;
            _onPresetDeleted = onPresetDeleted;
            _onExportPresetClick = onExportPresetClick;
            _onImportPresetClick = onImportPresetClick;
            _getString = getString;

            RebuildPresetsMenu();
        }

        private void OnAddPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            _onAddPresetClick?.Invoke();
            LlmPresetFlyout.Hide();
        }

        private void OnExportPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            _onExportPresetClick?.Invoke();
            LlmPresetFlyout.Hide();
        }

        private void OnImportPresetClickInPanel(object sender, RoutedEventArgs e)
        {
            _onImportPresetClick?.Invoke();
            LlmPresetFlyout.Hide();
        }

        private void OnPresetFlyoutOpened(object sender, object e)
        {
            RebuildPresetsMenu();
        }

        private void RebuildPresetsMenu()
        {
            if (LlmPresetListPanel == null || _getString == null) return;
            LlmPresetListPanel.Children.Clear();

            var buttonStyle = RightSidebarRoot.Resources["RightSidebarButtonStyle"] as Style;

            foreach (var presetName in _presetNames)
            {
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var selectBtn = new Button
                {
                    Content = presetName,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                string currentName = presetName;
                selectBtn.Click += (s, e) => {
                    _onPresetSelected?.Invoke(currentName);
                    LlmPresetFlyout.Hide();
                };
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
                editBtn.Click += (s, e) => {
                    _onPresetEdited?.Invoke(currentName);
                    LlmPresetFlyout.Hide();
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
                deleteBtn.Click += (s, e) => {
                    _onPresetDeleted?.Invoke(currentName);
                };
                Grid.SetColumn(deleteBtn, 2);
                rowGrid.Children.Add(deleteBtn);

                LlmPresetListPanel.Children.Add(rowGrid);
            }
        }

        private void OnRightTabViewSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                RightTabView.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to shift focus on right tab selection change: {ex.Message}");
            }
        }
    }
}
