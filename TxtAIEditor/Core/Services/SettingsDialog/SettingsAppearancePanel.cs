using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

            var section = SettingsDialogUi.CreateSection();
            SettingsDialogUi.AddLabel(section, getString("SettingsLanguage", "애플리케이션 언어 (Language)"));
            section.Children.Add(_languageCombo);
            SettingsDialogUi.AddLabel(section, getString("SettingsTheme", "앱/에디터 테마"));
            section.Children.Add(_themeCombo);
            SettingsDialogUi.AddLabel(section, getString("SettingsUiFontFamily", "UI 쉘 폰트"));
            section.Children.Add(_uiFontFamilyCombo);
            SettingsDialogUi.AddLabel(section, getString("SettingsFontFamily", "에디터 폰트"));
            section.Children.Add(_editorFontFamilyCombo);

            var editorSizeLabel = new TextBlock { Text = getString("SettingsFontSize", "에디터 글자 크기") + $" ({settings.FontSize:0}pt)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            section.Children.Add(editorSizeLabel);
            section.Children.Add(_editorSizeSlider);
            _editorSizeSlider.ValueChanged += (_, args) => editorSizeLabel.Text = getString("SettingsFontSize", "에디터 글자 크기") + $" ({args.NewValue:0}pt)";
            section.Children.Add(_customBgCheck);
            section.Children.Add(customBgDropdown);
            section.Children.Add(_customFgCheck);
            section.Children.Add(customFgDropdown);

            SettingsDialogUi.AddLabel(section, getString("SettingsPreviewFontFamily", "프리뷰 폰트"));
            section.Children.Add(_previewFontFamilyCombo);
            var previewSizeLabel = new TextBlock { Text = getString("SettingsPreviewFontSize", "프리뷰 글자 크기") + $" ({settings.PreviewFontSize:0}pt)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            section.Children.Add(previewSizeLabel);
            section.Children.Add(_previewSizeSlider);
            _previewSizeSlider.ValueChanged += (_, args) => previewSizeLabel.Text = getString("SettingsPreviewFontSize", "프리뷰 글자 크기") + $" ({args.NewValue:0}pt)";
            section.Children.Add(_previewBgCheck);
            section.Children.Add(previewBgDropdown);
            section.Children.Add(_previewFgCheck);
            section.Children.Add(previewFgDropdown);

            Content = section;
        }

        public void ApplyToSettings(EditorSettings settings)
        {
            settings.Language = _languageCombo.SelectedIndex switch
            {
                1 => "ko-KR",
                2 => "en-US",
                3 => "ja-JP",
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
        }

        private static ComboBox CreateLanguageCombo(EditorSettings settings, Func<string, string, string> getString)
        {
            var languageCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            languageCombo.Items.Add(getString("LanguageDefault", "Default (OS Language)"));
            languageCombo.Items.Add(getString("LanguageKorean", "한국어"));
            languageCombo.Items.Add(getString("LanguageEnglish", "English"));
            languageCombo.Items.Add(getString("LanguageJapanese", "日本語"));
            languageCombo.SelectedIndex = settings.Language switch
            {
                "ko-KR" => 1,
                "en-US" => 2,
                "ja-JP" => 3,
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
