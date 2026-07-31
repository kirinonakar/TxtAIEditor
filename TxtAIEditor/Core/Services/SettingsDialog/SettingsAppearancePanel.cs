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

            var section = new StackPanel { Spacing = 10, Width = 460, Padding = new Thickness(2, 6, 2, 2) };
            section.Children.Add(CreateGeneralCard(getString));
            section.Children.Add(CreateUiCard(getString));
            section.Children.Add(CreateEditorCard(getString, settings, customBgDropdown, customFgDropdown));
            section.Children.Add(CreatePreviewCard(getString, settings, previewBgDropdown, previewFgDropdown));
            Content = section;
        }

        private Border CreateCard(string title, UIElement content, string fontIconGlyph)
        {
            var container = new StackPanel { Spacing = 8 };

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 0, 0, 2)
            };

            if (!string.IsNullOrEmpty(fontIconGlyph))
            {
                headerPanel.Children.Add(new FontIcon
                {
                    Glyph = fontIconGlyph,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            headerPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            });

            container.Children.Add(headerPanel);
            container.Children.Add(content);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            card.Loaded += (s, e) =>
            {
                if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out object? bg) && bg is Brush bgBrush)
                {
                    card.Background = bgBrush;
                }
                else
                {
                    card.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(15, 128, 128, 128));
                }

                if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? border) && border is Brush borderBrush)
                {
                    card.BorderBrush = borderBrush;
                }
                else
                {
                    card.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 128, 128, 128));
                }
            };

            card.Child = container;
            return card;
        }

        private Border CreateSubGroup(string title, UIElement content)
        {
            var container = new StackPanel { Spacing = 6 };
            container.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
            });
            container.Children.Add(content);

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            border.Loaded += (s, e) =>
            {
                if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out object? bg) && bg is Brush bgBrush)
                {
                    border.Background = bgBrush;
                }
                else
                {
                    border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(10, 128, 128, 128));
                }

                if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? bBorder) && bBorder is Brush borderBrush)
                {
                    border.BorderBrush = borderBrush;
                }
                else
                {
                    border.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128));
                }
            };

            border.Child = container;
            return border;
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

            var colorPanel = new StackPanel { Spacing = 4 };
            colorPanel.Children.Add(_customBgCheck);
            colorPanel.Children.Add(customBgDropdown);
            colorPanel.Children.Add(_customFgCheck);
            colorPanel.Children.Add(customFgDropdown);

            var colorSubGroup = CreateSubGroup(
                getString("SettingsAppearanceGroupCustomColors", "커스텀 색상 설정"),
                colorPanel);
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

            var colorPanel = new StackPanel { Spacing = 4 };
            colorPanel.Children.Add(_previewBgCheck);
            colorPanel.Children.Add(previewBgDropdown);
            colorPanel.Children.Add(_previewFgCheck);
            colorPanel.Children.Add(previewFgDropdown);

            var colorSubGroup = CreateSubGroup(
                getString("SettingsAppearanceGroupCustomColors", "커스텀 색상 설정"),
                colorPanel);
            content.Children.Add(colorSubGroup);

            return CreateCard(
                getString("SettingsAppearanceGroupPreview", "마크다운 프리뷰 모양 & 색상"),
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
