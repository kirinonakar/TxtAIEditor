using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsAppearancePanel : UserControl
    {
        private readonly ComboBox _languageCombo;
        private readonly ComboBox _themeCombo;
        private readonly Slider _editorSizeSlider;
        private readonly Slider _previewSizeSlider;
        private readonly ComboBox _editorFontFamilyCombo;
        private readonly ComboBox _uiFontFamilyCombo;
        private readonly ComboBox _previewFontFamilyCombo;
        private readonly CheckBox _customBgCheck;
        private readonly CheckBox _customFgCheck;
        private readonly CheckBox _previewBgCheck;
        private readonly CheckBox _previewFgCheck;
        private readonly ColorPicker _customBgPicker;
        private readonly ColorPicker _customFgPicker;
        private readonly ColorPicker _previewBgPicker;
        private readonly ColorPicker _previewFgPicker;
        private readonly ComboBox _aozoraFontFamilyCombo;
        private readonly Slider _aozoraSizeSlider;
        private readonly CheckBox _aozoraBgCheck;
        private readonly CheckBox _aozoraFgCheck;
        private readonly ColorPicker _aozoraBgPicker;
        private readonly ColorPicker _aozoraFgPicker;
        private readonly ComboBox _agentFontFamilyCombo;
        private readonly Slider _agentSizeSlider;
        private readonly ComboBox _agentPromptFontFamilyCombo;
        private readonly Slider _agentPromptSizeSlider;
        private readonly Slider _rightPanelNormalWidthSlider;
        private readonly Slider _rightPanelExpandedWidthSlider;

        public SettingsAppearancePanel(
            EditorSettings settings,
            IReadOnlyList<string> fontFamilies,
            Func<string, string, string> getString)
        {
            _languageCombo = CreateLanguageCombo(settings, getString);

            int themeIdx = 0; // Dark
            if (settings.Theme == "Light") themeIdx = 1;
            else if (settings.Theme == "PastelDark") themeIdx = 2;

            _themeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = themeIdx };
            _themeCombo.Items.Add("Dark Theme (vs-dark)");
            _themeCombo.Items.Add("Light Theme (vs)");
            _themeCombo.Items.Add("Pastel Dark");

            _editorSizeSlider = new Slider { Minimum = 10, Maximum = 24, Value = settings.FontSize, StepFrequency = 1 };
            _previewSizeSlider = new Slider { Minimum = 10, Maximum = 24, Value = settings.PreviewFontSize, StepFrequency = 1 };
            _editorFontFamilyCombo = SettingsDialogUi.CreateFontComboBox(settings.FontFamily, fontFamilies);
            _uiFontFamilyCombo = SettingsDialogUi.CreateFontComboBox(settings.UiFontFamily, fontFamilies);
            _previewFontFamilyCombo = SettingsDialogUi.CreateFontComboBox(settings.PreviewFontFamily, fontFamilies);

            string defaultBg = settings.Theme == "Light" ? "#ffffff" : (settings.Theme == "PastelDark" ? "#24273a" : "#1e1e1e");
            string defaultFg = settings.Theme == "Light" ? "#111111" : (settings.Theme == "PastelDark" ? "#cad3f5" : "#d4d4d4");

            _customBgCheck = new CheckBox { Content = getString("SettingsUseCustomBg", "커스텀 에디터 배경색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.CustomBackgroundColor) };
            _customFgCheck = new CheckBox { Content = getString("SettingsUseCustomFg", "커스텀 에디터 글자색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.CustomForegroundColor) };
            var customBgDropdown = SettingsDialogUi.CreateColorDropdown(
                getString("SettingsUseCustomBg", "에디터 배경색"),
                SettingsDialogUi.ResolvePickerColor(settings.CustomBackgroundColor, defaultBg),
                out _customBgPicker);
            var customFgDropdown = SettingsDialogUi.CreateColorDropdown(
                getString("SettingsUseCustomFg", "에디터 글자색"),
                SettingsDialogUi.ResolvePickerColor(settings.CustomForegroundColor, defaultFg),
                out _customFgPicker);
            BindEnabled(_customBgCheck, customBgDropdown);
            BindEnabled(_customFgCheck, customFgDropdown);

            _previewBgCheck = new CheckBox { Content = getString("SettingsPreviewUseCustomBg", "커스텀 프리뷰 배경색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.PreviewCustomBackgroundColor) };
            _previewFgCheck = new CheckBox { Content = getString("SettingsPreviewUseCustomFg", "커스텀 프리뷰 글자색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.PreviewCustomForegroundColor) };
            var previewBgDropdown = SettingsDialogUi.CreateColorDropdown(
                getString("SettingsPreviewUseCustomBg", "프리뷰 배경색"),
                SettingsDialogUi.ResolvePickerColor(settings.PreviewCustomBackgroundColor, defaultBg),
                out _previewBgPicker);
            var previewFgDropdown = SettingsDialogUi.CreateColorDropdown(
                getString("SettingsPreviewUseCustomFg", "프리뷰 글자색"),
                SettingsDialogUi.ResolvePickerColor(settings.PreviewCustomForegroundColor, defaultFg),
                out _previewFgPicker);
            BindEnabled(_previewBgCheck, previewBgDropdown);
            BindEnabled(_previewFgCheck, previewFgDropdown);

            _aozoraFontFamilyCombo = SettingsDialogUi.CreateFontComboBox(settings.AozoraPreviewFontFamily, fontFamilies);
            _aozoraSizeSlider = new Slider { Minimum = 10, Maximum = 24, Value = settings.AozoraPreviewFontSize, StepFrequency = 1 };
            _aozoraBgCheck = new CheckBox { Content = getString("SettingsAozoraUseCustomBg", "커스텀 Aozora 배경색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.AozoraPreviewCustomBackgroundColor) };
            _aozoraFgCheck = new CheckBox { Content = getString("SettingsAozoraUseCustomFg", "커스텀 Aozora 글자색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.AozoraPreviewCustomForegroundColor) };
            var aozoraBgDropdown = SettingsDialogUi.CreateColorDropdown(
                getString("SettingsAozoraUseCustomBg", "Aozora 배경색"),
                SettingsDialogUi.ResolvePickerColor(settings.AozoraPreviewCustomBackgroundColor, defaultBg),
                out _aozoraBgPicker);
            var aozoraFgDropdown = SettingsDialogUi.CreateColorDropdown(
                getString("SettingsAozoraUseCustomFg", "Aozora 글자색"),
                SettingsDialogUi.ResolvePickerColor(settings.AozoraPreviewCustomForegroundColor, defaultFg),
                out _aozoraFgPicker);
            BindEnabled(_aozoraBgCheck, aozoraBgDropdown);
            BindEnabled(_aozoraFgCheck, aozoraFgDropdown);

            _agentFontFamilyCombo = SettingsDialogUi.CreateFontComboBox(settings.AgentFontFamily, fontFamilies);
            _agentSizeSlider = new Slider { Minimum = 10, Maximum = 24, Value = settings.AgentFontSize, StepFrequency = 1 };
            _agentPromptFontFamilyCombo = SettingsDialogUi.CreateFontComboBox(settings.AgentPromptFontFamily, fontFamilies);
            _agentPromptSizeSlider = new Slider { Minimum = 10, Maximum = 24, Value = settings.AgentPromptFontSize, StepFrequency = 1 };
            _rightPanelNormalWidthSlider = new Slider { Minimum = 150, Maximum = 1200, Value = settings.RightSidebarNormalWidth, StepFrequency = 10 };
            _rightPanelExpandedWidthSlider = new Slider { Minimum = 150, Maximum = 1200, Value = settings.RightSidebarExpandedWidth, StepFrequency = 10 };

            var section = new StackPanel { Spacing = 10, Width = 460, Padding = new Thickness(2, 6, 2, 2) };
            section.Children.Add(CreateGeneralCard(getString));
            section.Children.Add(CreateUiCard(getString));
            section.Children.Add(CreateEditorCard(getString, settings, customBgDropdown, customFgDropdown));
            section.Children.Add(CreatePreviewCard(getString, settings, previewBgDropdown, previewFgDropdown));
            section.Children.Add(CreateAozoraCard(getString, settings, aozoraBgDropdown, aozoraFgDropdown));
            section.Children.Add(CreateAgentCard(getString, settings));
            section.Children.Add(CreateRightPanelWidthCard(getString, settings));
            Content = section;
        }

        private static Border CreateCard(string title, UIElement content, string fontIconGlyph)
        {
            return SettingsDialogUi.CreateCard(title, content, fontIconGlyph);
        }

        private static Border CreateSubGroup(string title, UIElement content)
        {
            return SettingsDialogUi.CreateSubGroup(title, content);
        }

        private Border CreateGeneralCard(Func<string, string, string> getString)
        {
            var content = new StackPanel { Spacing = 6 };
            SettingsDialogUi.AddLabel(content, getString("SettingsLanguage", "애플리케이션 언어 (Language)"));
            content.Children.Add(_languageCombo);
            SettingsDialogUi.AddLabel(content, getString("SettingsTheme", "앱/에디터 테마"));
            content.Children.Add(_themeCombo);

            return CreateCard(
                getString("SettingsAppearanceGroupGeneral", "언어 & 테마"),
                content,
                "\uE774");
        }

        private Border CreateUiCard(Func<string, string, string> getString)
        {
            var content = new StackPanel { Spacing = 6 };
            SettingsDialogUi.AddLabel(content, getString("SettingsUiFontFamily", "UI 쉘 폰트"));
            content.Children.Add(_uiFontFamilyCombo);

            return CreateCard(
                getString("SettingsAppearanceGroupUi", "UI 쉘 폰트"),
                content,
                "\uE8B9");
        }

        private Border CreateEditorCard(
            Func<string, string, string> getString,
            EditorSettings settings,
            UIElement customBgDropdown,
            UIElement customFgDropdown)
        {
            var content = new StackPanel { Spacing = 6 };

            SettingsDialogUi.AddLabel(content, getString("SettingsFontFamily", "에디터 폰트"));
            content.Children.Add(_editorFontFamilyCombo);

            var editorSizeLabel = new TextBlock
            {
                Text = getString("SettingsFontSize", "에디터 글자 크기") + $" ({settings.FontSize:0}pt)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            content.Children.Add(editorSizeLabel);
            content.Children.Add(_editorSizeSlider);
            _editorSizeSlider.ValueChanged += (_, args) => editorSizeLabel.Text = getString("SettingsFontSize", "에디터 글자 크기") + $" ({args.NewValue:0}pt)";

            var colorGrid = new Grid();
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Pixel) });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bgStack = new StackPanel { Spacing = 4 };
            bgStack.Children.Add(_customBgCheck);
            bgStack.Children.Add(customBgDropdown);
            Grid.SetColumn(bgStack, 0);

            var fgStack = new StackPanel { Spacing = 4 };
            fgStack.Children.Add(_customFgCheck);
            fgStack.Children.Add(customFgDropdown);
            Grid.SetColumn(fgStack, 2);

            colorGrid.Children.Add(bgStack);
            colorGrid.Children.Add(fgStack);

            var colorSubGroup = CreateSubGroup(
                getString("SettingsAppearanceGroupCustomColors", "커스텀 색상 설정"),
                colorGrid);
            content.Children.Add(colorSubGroup);

            return CreateCard(
                getString("SettingsAppearanceGroupEditor", "에디터 모양 & 색상"),
                content,
                "\uE8AC");
        }

        private Border CreatePreviewCard(
            Func<string, string, string> getString,
            EditorSettings settings,
            UIElement previewBgDropdown,
            UIElement previewFgDropdown)
        {
            var content = new StackPanel { Spacing = 6 };

            SettingsDialogUi.AddLabel(content, getString("SettingsPreviewFontFamily", "프리뷰 폰트"));
            content.Children.Add(_previewFontFamilyCombo);

            var previewSizeLabel = new TextBlock
            {
                Text = getString("SettingsPreviewFontSize", "프리뷰 글자 크기") + $" ({settings.PreviewFontSize:0}pt)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            content.Children.Add(previewSizeLabel);
            content.Children.Add(_previewSizeSlider);
            _previewSizeSlider.ValueChanged += (_, args) => previewSizeLabel.Text = getString("SettingsPreviewFontSize", "프리뷰 글자 크기") + $" ({args.NewValue:0}pt)";

            var colorGrid = new Grid();
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Pixel) });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bgStack = new StackPanel { Spacing = 4 };
            bgStack.Children.Add(_previewBgCheck);
            bgStack.Children.Add(previewBgDropdown);
            Grid.SetColumn(bgStack, 0);

            var fgStack = new StackPanel { Spacing = 4 };
            fgStack.Children.Add(_previewFgCheck);
            fgStack.Children.Add(previewFgDropdown);
            Grid.SetColumn(fgStack, 2);

            colorGrid.Children.Add(bgStack);
            colorGrid.Children.Add(fgStack);

            var colorSubGroup = CreateSubGroup(
                getString("SettingsAppearanceGroupCustomColors", "커스텀 색상 설정"),
                colorGrid);
            content.Children.Add(colorSubGroup);

            return CreateCard(
                getString("SettingsAppearanceGroupPreview", "마크다운 프리뷰 모양 & 색상"),
                content,
                "\uE8A5");
        }

        private Border CreateAozoraCard(
            Func<string, string, string> getString,
            EditorSettings settings,
            UIElement aozoraBgDropdown,
            UIElement aozoraFgDropdown)
        {
            var content = new StackPanel { Spacing = 6 };

            SettingsDialogUi.AddLabel(content, getString("SettingsAozoraFontFamily", "Aozora 프리뷰 폰트"));
            content.Children.Add(_aozoraFontFamilyCombo);

            var aozoraSizeLabel = new TextBlock
            {
                Text = getString("SettingsAozoraFontSize", "Aozora 프리뷰 글자 크기") + $" ({settings.AozoraPreviewFontSize:0}pt)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            content.Children.Add(aozoraSizeLabel);
            content.Children.Add(_aozoraSizeSlider);
            _aozoraSizeSlider.ValueChanged += (_, args) => aozoraSizeLabel.Text = getString("SettingsAozoraFontSize", "Aozora 프리뷰 글자 크기") + $" ({args.NewValue:0}pt)";

            var colorGrid = new Grid();
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Pixel) });
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bgStack = new StackPanel { Spacing = 4 };
            bgStack.Children.Add(_aozoraBgCheck);
            bgStack.Children.Add(aozoraBgDropdown);
            Grid.SetColumn(bgStack, 0);

            var fgStack = new StackPanel { Spacing = 4 };
            fgStack.Children.Add(_aozoraFgCheck);
            fgStack.Children.Add(aozoraFgDropdown);
            Grid.SetColumn(fgStack, 2);

            colorGrid.Children.Add(bgStack);
            colorGrid.Children.Add(fgStack);

            var colorSubGroup = CreateSubGroup(
                getString("SettingsAppearanceGroupCustomColors", "커스텀 색상 설정"),
                colorGrid);
            content.Children.Add(colorSubGroup);

            return CreateCard(
                getString("SettingsAppearanceGroupAozora", "Aozora 프리뷰 모양 & 색상"),
                content,
                "\uE8A5");
        }

        private Border CreateAgentCard(Func<string, string, string> getString, EditorSettings settings)
        {
            var content = new StackPanel { Spacing = 6 };

            SettingsDialogUi.AddLabel(content, getString("SettingsAgentOutputFontFamily", "에이전트 출력 폰트"));
            content.Children.Add(_agentFontFamilyCombo);

            var agentSizeLabel = new TextBlock
            {
                Text = getString("SettingsAgentOutputFontSize", "에이전트 출력 글자 크기") + $" ({settings.AgentFontSize:0}pt)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            content.Children.Add(agentSizeLabel);
            content.Children.Add(_agentSizeSlider);
            _agentSizeSlider.ValueChanged += (_, args) => agentSizeLabel.Text = getString("SettingsAgentOutputFontSize", "에이전트 출력 글자 크기") + $" ({args.NewValue:0}pt)";

            SettingsDialogUi.AddLabel(content, getString("SettingsAgentPromptFontFamily", "사용자 프롬프트 폰트"));
            content.Children.Add(_agentPromptFontFamilyCombo);

            var promptSizeLabel = new TextBlock
            {
                Text = getString("SettingsAgentPromptFontSize", "사용자 프롬프트 글자 크기") + $" ({settings.AgentPromptFontSize:0}pt)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            content.Children.Add(promptSizeLabel);
            content.Children.Add(_agentPromptSizeSlider);
            _agentPromptSizeSlider.ValueChanged += (_, args) => promptSizeLabel.Text = getString("SettingsAgentPromptFontSize", "사용자 프롬프트 글자 크기") + $" ({args.NewValue:0}pt)";

            return CreateCard(
                getString("SettingsAppearanceGroupAgent", "Agent 창 폰트"),
                content,
                "\uE8B9");
        }

        private Border CreateRightPanelWidthCard(Func<string, string, string> getString, EditorSettings settings)
        {
            var content = new StackPanel { Spacing = 6 };

            var normalWidthLabel = new TextBlock
            {
                Text = getString("SettingsRightPanelWidthNormal", "기본 우측 패널 너비") + $" ({settings.RightSidebarNormalWidth:0}px)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            content.Children.Add(normalWidthLabel);
            content.Children.Add(_rightPanelNormalWidthSlider);
            _rightPanelNormalWidthSlider.ValueChanged += (_, args) => normalWidthLabel.Text = getString("SettingsRightPanelWidthNormal", "기본 우측 패널 너비") + $" ({args.NewValue:0}px)";

            var expandedWidthLabel = new TextBlock
            {
                Text = getString("SettingsRightPanelWidthExpanded", "프리뷰 늘리기 시 우측 패널 너비") + $" ({settings.RightSidebarExpandedWidth:0}px)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            content.Children.Add(expandedWidthLabel);
            content.Children.Add(_rightPanelExpandedWidthSlider);
            _rightPanelExpandedWidthSlider.ValueChanged += (_, args) => expandedWidthLabel.Text = getString("SettingsRightPanelWidthExpanded", "프리뷰 늘리기 시 우측 패널 너비") + $" ({args.NewValue:0}px)";

            return CreateCard(
                getString("SettingsRightPanelWidthGroup", "우측 패널 너비"),
                content,
                "\uE8A5");
        }

        public void ApplyToSettings(EditorSettings settings)
        {
            settings.Language = _languageCombo.SelectedIndex switch
            {
                1 => "ko-KR",
                2 => "en-US",
                3 => "ja-JP",
                4 => "zh-Hans",
                5 => "zh-Hant",
                _ => "Default"
            };
            settings.Theme = _themeCombo.SelectedIndex switch
            {
                0 => "Dark",
                1 => "Light",
                2 => "PastelDark",
                _ => "Dark"
            };
            settings.FontSize = _editorSizeSlider.Value;
            settings.CustomBackgroundColor = _customBgCheck.IsChecked == true ? SettingsDialogUi.ColorToHex(_customBgPicker.Color) : string.Empty;
            settings.CustomForegroundColor = _customFgCheck.IsChecked == true ? SettingsDialogUi.ColorToHex(_customFgPicker.Color) : string.Empty;
            settings.FontFamily = SettingsDialogUi.GetSelectedComboText(_editorFontFamilyCombo, settings.FontFamily);
            settings.UiFontFamily = SettingsDialogUi.GetSelectedComboText(_uiFontFamilyCombo, settings.UiFontFamily);
            settings.PreviewFontFamily = SettingsDialogUi.GetSelectedComboText(_previewFontFamilyCombo, settings.PreviewFontFamily);
            settings.PreviewFontSize = _previewSizeSlider.Value;
            settings.PreviewCustomBackgroundColor = _previewBgCheck.IsChecked == true ? SettingsDialogUi.ColorToHex(_previewBgPicker.Color) : string.Empty;
            settings.PreviewCustomForegroundColor = _previewFgCheck.IsChecked == true ? SettingsDialogUi.ColorToHex(_previewFgPicker.Color) : string.Empty;
            settings.AozoraPreviewFontFamily = SettingsDialogUi.GetSelectedComboText(_aozoraFontFamilyCombo, settings.AozoraPreviewFontFamily);
            settings.AozoraPreviewFontSize = _aozoraSizeSlider.Value;
            settings.AozoraPreviewCustomBackgroundColor = _aozoraBgCheck.IsChecked == true ? SettingsDialogUi.ColorToHex(_aozoraBgPicker.Color) : string.Empty;
            settings.AozoraPreviewCustomForegroundColor = _aozoraFgCheck.IsChecked == true ? SettingsDialogUi.ColorToHex(_aozoraFgPicker.Color) : string.Empty;
            settings.AgentFontFamily = SettingsDialogUi.GetSelectedComboText(_agentFontFamilyCombo, settings.AgentFontFamily);
            settings.AgentFontSize = _agentSizeSlider.Value;
            settings.AgentPromptFontFamily = SettingsDialogUi.GetSelectedComboText(_agentPromptFontFamilyCombo, settings.AgentPromptFontFamily);
            settings.AgentPromptFontSize = _agentPromptSizeSlider.Value;
            settings.RightSidebarNormalWidth = _rightPanelNormalWidthSlider.Value;
            settings.RightSidebarExpandedWidth = _rightPanelExpandedWidthSlider.Value;
        }

        private static ComboBox CreateLanguageCombo(EditorSettings settings, Func<string, string, string> getString)
        {
            var languageCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            languageCombo.Items.Add(getString("LanguageDefault", "Default (OS Language)"));
            languageCombo.Items.Add(getString("LanguageKorean", "한국어"));
            languageCombo.Items.Add(getString("LanguageEnglish", "English"));
            languageCombo.Items.Add(getString("LanguageJapanese", "日本語"));
            languageCombo.Items.Add(getString("LanguageChineseSimplified", "简体中文"));
            languageCombo.Items.Add(getString("LanguageChineseTraditional", "繁體中文"));
            languageCombo.SelectedIndex = settings.Language switch
            {
                "ko-KR" => 1,
                "en-US" => 2,
                "ja-JP" => 3,
                "zh-Hans" => 4,
                "zh-CN" => 4,
                "zh-SG" => 4,
                "zh-Hant" => 5,
                "zh-TW" => 5,
                "zh-HK" => 5,
                "zh-MO" => 5,
                _ => 0
            };
            return languageCombo;
        }

        private static void BindEnabled(CheckBox checkBox, Control target)
        {
            target.IsEnabled = checkBox.IsChecked == true;
            checkBox.Checked += (_, __) => target.IsEnabled = true;
            checkBox.Unchecked += (_, __) => target.IsEnabled = false;
        }
    }
}
