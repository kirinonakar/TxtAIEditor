using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    public sealed class SettingsDialogService : ISettingsDialogService
    {
        private sealed record ToolbarOrderItem(string Id, string Label);
        private sealed record TerminalProfileChoice(string Id, string Label);

        private static IReadOnlyList<string>? _installedFontFamiliesCache;
        private readonly ILLMService _llmService;

        public SettingsDialogService(ILLMService llmService)
        {
            _llmService = llmService;
        }

        public async Task<SettingsDialogResult> ShowAsync(
            EditorSettings settings,
            XamlRoot xamlRoot,
            Func<string, string, string> getString)
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

            var themeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = settings.Theme == "Dark" ? 0 : 1 };
            themeCombo.Items.Add("Dark Theme (vs-dark)");
            themeCombo.Items.Add("Light Theme (vs)");

            var sizeSlider = new Slider { Minimum = 10, Maximum = 24, Value = settings.FontSize, StepFrequency = 1 };
            var previewSizeSlider = new Slider { Minimum = 10, Maximum = 24, Value = settings.PreviewFontSize, StepFrequency = 1 };
            var terminalSizeSlider = new Slider { Minimum = 8, Maximum = 36, Value = Math.Clamp(settings.TerminalFontSize, 8, 36), StepFrequency = 1 };

            var fontFamilies = GetInstalledFontFamilies();
            var fontFamilyCombo = CreateFontComboBox(settings.FontFamily, fontFamilies);
            var uiFontFamilyCombo = CreateFontComboBox(settings.UiFontFamily, fontFamilies);
            var terminalFontFamilyCombo = CreateFontComboBox(settings.TerminalFontFamily, fontFamilies);
            var customBgCheck = new CheckBox { Content = getString("SettingsUseCustomBg", "커스텀 에디터 배경색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.CustomBackgroundColor) };
            var customFgCheck = new CheckBox { Content = getString("SettingsUseCustomFg", "커스텀 에디터 글자색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.CustomForegroundColor) };
            var customBgDropdown = CreateColorDropdown(getString("SettingsUseCustomBg", "에디터 배경색"), ResolvePickerColor(settings.CustomBackgroundColor, settings.Theme == "Light" ? "#ffffff" : "#1e1e1e"), out var customBgPicker);
            var customFgDropdown = CreateColorDropdown(getString("SettingsUseCustomFg", "에디터 글자색"), ResolvePickerColor(settings.CustomForegroundColor, settings.Theme == "Light" ? "#111111" : "#d4d4d4"), out var customFgPicker);
            customBgDropdown.IsEnabled = customBgCheck.IsChecked == true;
            customFgDropdown.IsEnabled = customFgCheck.IsChecked == true;
            customBgCheck.Checked += (_, __) => customBgDropdown.IsEnabled = true;
            customBgCheck.Unchecked += (_, __) => customBgDropdown.IsEnabled = false;
            customFgCheck.Checked += (_, __) => customFgDropdown.IsEnabled = true;
            customFgCheck.Unchecked += (_, __) => customFgDropdown.IsEnabled = false;

            var previewFontFamilyCombo = CreateFontComboBox(settings.PreviewFontFamily, fontFamilies);
            var previewBgCheck = new CheckBox { Content = getString("SettingsPreviewUseCustomBg", "커스텀 프리뷰 배경색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.PreviewCustomBackgroundColor) };
            var previewFgCheck = new CheckBox { Content = getString("SettingsPreviewUseCustomFg", "커스텀 프리뷰 글자색 사용"), IsChecked = !string.IsNullOrWhiteSpace(settings.PreviewCustomForegroundColor) };
            var previewBgDropdown = CreateColorDropdown(getString("SettingsPreviewUseCustomBg", "프리뷰 배경색"), ResolvePickerColor(settings.PreviewCustomBackgroundColor, settings.Theme == "Light" ? "#ffffff" : "#1e1e1e"), out var previewBgPicker);
            var previewFgDropdown = CreateColorDropdown(getString("SettingsPreviewUseCustomFg", "프리뷰 글자색"), ResolvePickerColor(settings.PreviewCustomForegroundColor, settings.Theme == "Light" ? "#111111" : "#d4d4d4"), out var previewFgPicker);
            previewBgDropdown.IsEnabled = previewBgCheck.IsChecked == true;
            previewFgDropdown.IsEnabled = previewFgCheck.IsChecked == true;
            previewBgCheck.Checked += (_, __) => previewBgDropdown.IsEnabled = true;
            previewBgCheck.Unchecked += (_, __) => previewBgDropdown.IsEnabled = false;
            previewFgCheck.Checked += (_, __) => previewFgDropdown.IsEnabled = true;
            previewFgCheck.Unchecked += (_, __) => previewFgDropdown.IsEnabled = false;

            var wordWrapCheck = new CheckBox { Content = getString("SettingsWordWrap", "기본 Word Wrap 켜기"), IsChecked = settings.WordWrap };
            var bracketColorCheck = new CheckBox { Content = getString("SettingsBracketPair", "괄호 쌍 색상화 활성화"), IsChecked = settings.BracketPairColorization };
            var autocompleteEnterCheck = new CheckBox { Content = getString("SettingsAutocompleteEnter", "Enter로 자동완성"), IsChecked = settings.AutocompleteOnEnter };
            var autocompleteTabCheck = new CheckBox { Content = getString("SettingsAutocompleteTab", "Tab으로 자동완성"), IsChecked = settings.AutocompleteOnTab };
            var autoSaveCheck = new CheckBox { Content = getString("SettingsAutoSave", "Autosave 사용"), IsChecked = settings.AutoSave };
            var defaultMarkdownCheck = new CheckBox { Content = getString("SettingsLivePreview", "실시간 미리보기 기본 활성화"), IsChecked = settings.DefaultMarkdownEnabled };
            var defaultMarkdownToolbarCheck = new CheckBox { Content = getString("SettingsMarkdownToolbar", "기본 마크다운 툴바 활성화"), IsChecked = settings.DefaultMarkdownToolbarEnabled };
            var tabSizeBox = new TextBox { PlaceholderText = "예: 4", Text = settings.TabSize.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
            var terminalProfileCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var profile in TerminalShellProfile.GetProfiles())
            {
                string label = profile.IsAvailable
                    ? profile.DisplayName
                    : $"{profile.DisplayName} ({getString("SettingsTerminalNotFound", "설치되지 않음")})";
                terminalProfileCombo.Items.Add(new TerminalProfileChoice(profile.Id, label));
            }
            terminalProfileCombo.DisplayMemberPath = nameof(TerminalProfileChoice.Label);
            string selectedTerminalProfile = TerminalShellProfile.NormalizeId(settings.TerminalProfile);
            terminalProfileCombo.SelectedItem = terminalProfileCombo.Items
                .OfType<TerminalProfileChoice>()
                .FirstOrDefault(choice => choice.Id.Equals(selectedTerminalProfile, StringComparison.OrdinalIgnoreCase))
                ?? terminalProfileCombo.Items.OfType<TerminalProfileChoice>().FirstOrDefault();

            string[] providerNames = { "Gemini", "OpenAI", "OpenRouter", "LM Studio" };
            int providerIndex = Array.FindIndex(providerNames, p => p.Equals(settings.LlmProvider, StringComparison.OrdinalIgnoreCase));
            if (providerIndex < 0) providerIndex = 1;

            var llmProviderCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var providerName in providerNames)
            {
                llmProviderCombo.Items.Add(providerName);
            }
            llmProviderCombo.SelectedIndex = providerIndex;

            var llmEndpointBox = new TextBox { PlaceholderText = getString("SettingsLlmEndpointPlaceholder", "예: http://localhost:1234/v1"), Text = settings.LlmEndpoint, HorizontalAlignment = HorizontalAlignment.Stretch };
            var llmModelCombo = new ComboBox { PlaceholderText = getString("SettingsLlmSelectModel", "모델 선택"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = true, Tag = "LlmModelCombo" };
            llmModelCombo.Loaded += (s, e) => ApplyEditableComboBoxVisualStyles(llmModelCombo);
            var llmApiKeyBox = new PasswordBox { PasswordChar = "●", PlaceholderText = getString("SettingsLlmApiKeyPlaceholder", "API Key 입력 (비워두면 저장된 Key 삭제)"), HorizontalAlignment = HorizontalAlignment.Stretch };
            llmApiKeyBox.Password = await _llmService.GetApiKeyAsync(providerNames[providerIndex]);

            var confirmBeforeSendingCheck = new CheckBox { Content = getString("SettingsLlmConfirmBeforeSending", "전송 전 확인"), IsChecked = settings.LlmConfirmBeforeSending };

            var sourceLangCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            sourceLangCombo.Items.Add(getString("LlmLangAutoDetect", "자동 감지 (Auto Detect)"));
            sourceLangCombo.Items.Add(getString("LlmLangKorean", "한국어 (Korean)"));
            sourceLangCombo.Items.Add(getString("LlmLangEnglish", "영어 (English)"));
            sourceLangCombo.Items.Add(getString("LlmLangJapanese", "일본어 (Japanese)"));
            sourceLangCombo.Items.Add(getString("LlmLangChinese", "중국어 (Chinese)"));
            sourceLangCombo.Items.Add(getString("LlmLangFrench", "프랑스어 (French)"));
            sourceLangCombo.Items.Add(getString("LlmLangSpanish", "스페인어 (Spanish)"));
            sourceLangCombo.Items.Add(getString("LlmLangGerman", "독일어 (German)"));

            sourceLangCombo.SelectedIndex = settings.LlmSourceLanguage switch
            {
                "Korean" => 1,
                "English" => 2,
                "Japanese" => 3,
                "Chinese" => 4,
                "French" => 5,
                "Spanish" => 6,
                "German" => 7,
                _ => 0
            };

            var targetLangCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            targetLangCombo.Items.Add(getString("LlmLangKorean", "한국어 (Korean)"));
            targetLangCombo.Items.Add(getString("LlmLangEnglish", "영어 (English)"));
            targetLangCombo.Items.Add(getString("LlmLangJapanese", "일본어 (Japanese)"));
            targetLangCombo.Items.Add(getString("LlmLangChinese", "중국어 (Chinese)"));
            targetLangCombo.Items.Add(getString("LlmLangFrench", "프랑스어 (French)"));
            targetLangCombo.Items.Add(getString("LlmLangSpanish", "스페인어 (Spanish)"));
            targetLangCombo.Items.Add(getString("LlmLangGerman", "독일어 (German)"));

            targetLangCombo.SelectedIndex = settings.LlmTargetLanguage switch
            {
                "English" => 1,
                "Japanese" => 2,
                "Chinese" => 3,
                "French" => 4,
                "Spanish" => 5,
                "German" => 6,
                _ => 0
            };

            var refreshLmStudioModelsButton = new Button { Content = getString("SettingsLlmLoadModels", "LM Studio 모델 불러오기"), HorizontalAlignment = HorizontalAlignment.Stretch };
            var llmModelStatusText = new TextBlock
            {
                Text = getString("SettingsLlmInfo", "LM Studio는 서버가 켜져 있을 때 http://localhost:1234/v1/models 에서 모델 목록을 불러옵니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };

            string GetSelectedProviderName() => llmProviderCombo.SelectedItem as string ?? "OpenAI";

            void AddModelChoice(string model)
            {
                if (!string.IsNullOrWhiteSpace(model) && !llmModelCombo.Items.Contains(model))
                {
                    llmModelCombo.Items.Add(model);
                }
            }

            void SelectModelChoice(string model)
            {
                AddModelChoice(model);
                if (!string.IsNullOrWhiteSpace(model))
                {
                    llmModelCombo.SelectedItem = model;
                }
                else if (llmModelCombo.Items.Count > 0)
                {
                    llmModelCombo.SelectedIndex = 0;
                }
            }

            void PopulateModelChoices(string provider, string selectedModel)
            {
                llmModelCombo.Items.Clear();

                if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    AddModelChoice("gemini-flash-lite-latest");
                    AddModelChoice("gemini-flash-latest");
                    AddModelChoice("gemini-pro-latest");
                    AddModelChoice("gemma-4-26b-a4b-it");
                    AddModelChoice("gemma-4-31b-it");

                    string target = !string.IsNullOrEmpty(settings.LlmModelGemini) ? settings.LlmModelGemini : selectedModel;
                    if (string.IsNullOrEmpty(target) ||
                        (!target.Equals("gemini-flash-lite-latest", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("gemini-flash-latest", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("gemini-pro-latest", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("gemma-4-26b-a4b-it", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("gemma-4-31b-it", StringComparison.OrdinalIgnoreCase)))
                    {
                        target = "gemini-flash-lite-latest";
                    }
                    SelectModelChoice(target);
                }
                else if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    AddModelChoice("gpt-5.5");
                    string target = !string.IsNullOrEmpty(settings.LlmModelOpenAI) ? settings.LlmModelOpenAI : selectedModel;
                    if (string.IsNullOrEmpty(target) || !target.Equals("gpt-5.5", StringComparison.OrdinalIgnoreCase))
                    {
                        target = "gpt-5.5";
                    }
                    SelectModelChoice(target);
                }
                else if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
                {
                    AddModelChoice("meta-llama/llama-3.3-70b-instruct:free");
                    AddModelChoice("deepseek/deepseek-chat");
                    AddModelChoice("google/gemini-2.5-flash");
                    AddModelChoice("anthropic/claude-3.5-sonnet");

                    string target = !string.IsNullOrEmpty(settings.LlmModelOpenRouter) ? settings.LlmModelOpenRouter : selectedModel;
                    if (string.IsNullOrEmpty(target) ||
                        (!target.Equals("meta-llama/llama-3.3-70b-instruct:free", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("deepseek/deepseek-chat", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("google/gemini-2.5-flash", StringComparison.OrdinalIgnoreCase) &&
                         !target.Equals("anthropic/claude-3.5-sonnet", StringComparison.OrdinalIgnoreCase)))
                    {
                        AddModelChoice(target);
                    }
                    SelectModelChoice(target);
                }
                else if (provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
                {
                    string target = !string.IsNullOrEmpty(settings.LlmModelLmStudio) ? settings.LlmModelLmStudio : selectedModel;
                    SelectModelChoice(target);
                }
            }

            bool IsKnownDefaultEndpoint(string endpoint)
            {
                return string.IsNullOrWhiteSpace(endpoint) ||
                       endpoint.Equals("https://api.openai.com/v1", StringComparison.OrdinalIgnoreCase) ||
                       endpoint.Equals("https://openrouter.ai/api/v1", StringComparison.OrdinalIgnoreCase) ||
                       endpoint.Equals("http://localhost:1234/v1", StringComparison.OrdinalIgnoreCase) ||
                       endpoint.Equals("https://generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
            }

            void ApplyProviderDefaults(string provider)
            {
                if (!IsKnownDefaultEndpoint(llmEndpointBox.Text.Trim()))
                {
                    return;
                }

                llmEndpointBox.Text = provider switch
                {
                    "LM Studio" => "http://localhost:1234/v1",
                    "OpenAI" => "https://api.openai.com/v1",
                    "OpenRouter" => "https://openrouter.ai/api/v1",
                    "Gemini" => "https://generativelanguage.googleapis.com",
                    _ => llmEndpointBox.Text
                };
            }

            async Task RefreshLmStudioModelsAsync()
            {
                string provider = GetSelectedProviderName();
                bool isLmStudio = provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase);
                bool isOpenRouter = provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase);

                if (!isLmStudio && !isOpenRouter)
                {
                    llmModelStatusText.Text = getString("SettingsLlmLoadModelsNotSupported", "해당 공급자는 모델 불러오기를 지원하지 않습니다.");
                    return;
                }

                try
                {
                    refreshLmStudioModelsButton.IsEnabled = false;
                    llmModelStatusText.Text = string.Format(getString("SettingsLlmLoadingModelsFormat", "{0} 모델 목록을 불러오는 중입니다..."), provider);
                    var models = await FetchLmStudioModelsAsync(llmEndpointBox.Text.Trim());

                    llmModelCombo.Items.Clear();
                    foreach (var model in models)
                    {
                        AddModelChoice(model);
                    }

                    string targetModel = isLmStudio
                        ? (!string.IsNullOrEmpty(settings.LlmModelLmStudio) ? settings.LlmModelLmStudio : settings.LlmModel)
                        : (!string.IsNullOrEmpty(settings.LlmModelOpenRouter) ? settings.LlmModelOpenRouter : settings.LlmModel);

                    SelectModelChoice(models.Contains(targetModel) ? targetModel : models.FirstOrDefault() ?? targetModel);
                    llmModelStatusText.Text = models.Count > 0
                        ? string.Format(getString("SettingsLlmModelsLoadedFormat", "{0}개 모델을 불러왔습니다."), models.Count)
                        : string.Format(getString("SettingsLlmNoModelsFoundFormat", "{0}에서 사용 가능한 모델을 찾지 못했습니다."), provider);
                }
                catch (Exception ex)
                {
                    string targetModel = isLmStudio
                        ? (!string.IsNullOrEmpty(settings.LlmModelLmStudio) ? settings.LlmModelLmStudio : settings.LlmModel)
                        : (!string.IsNullOrEmpty(settings.LlmModelOpenRouter) ? settings.LlmModelOpenRouter : settings.LlmModel);
                    SelectModelChoice(targetModel);
                    llmModelStatusText.Text = string.Format(getString("SettingsLlmLoadModelsFailedFormat", "{0} 모델 목록을 불러오지 못했습니다: {1}"), provider, ex.Message);
                }
                finally
                {
                    refreshLmStudioModelsButton.IsEnabled = true;
                }
            }

            void UpdateModelRefreshButtonVisibility()
            {
                string provider = GetSelectedProviderName();
                if (provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
                {
                    refreshLmStudioModelsButton.Content = getString("SettingsLlmLoadModels", "LM Studio 모델 불러오기");
                    refreshLmStudioModelsButton.Visibility = Visibility.Visible;
                    llmModelStatusText.Text = getString("SettingsLlmInfo", "LM Studio는 서버가 켜져 있을 때 http://localhost:1234/v1/models 에서 모델 목록을 불러옵니다.");
                    llmModelStatusText.Visibility = Visibility.Visible;
                }
                else if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
                {
                    refreshLmStudioModelsButton.Content = getString("SettingsLlmLoadOpenRouterModels", "OpenRouter 모델 불러오기");
                    refreshLmStudioModelsButton.Visibility = Visibility.Visible;
                    llmModelStatusText.Text = getString("SettingsLlmOpenRouterInfo", "OpenRouter는 https://openrouter.ai/api/v1/models 에서 모델 목록을 불러옵니다.");
                    llmModelStatusText.Visibility = Visibility.Visible;
                }
                else
                {
                    refreshLmStudioModelsButton.Visibility = Visibility.Collapsed;
                    llmModelStatusText.Visibility = Visibility.Collapsed;
                }
            }

            PopulateModelChoices(GetSelectedProviderName(), settings.LlmModel);
            UpdateModelRefreshButtonVisibility();

            llmProviderCombo.SelectionChanged += async (_, __) =>
            {
                string provider = GetSelectedProviderName();
                ApplyProviderDefaults(provider);

                string targetModel = settings.LlmModel;
                if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
                {
                    targetModel = !string.IsNullOrEmpty(settings.LlmModelGemini) ? settings.LlmModelGemini : "gemini-flash-lite-latest";
                }
                else if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    targetModel = !string.IsNullOrEmpty(settings.LlmModelOpenAI) ? settings.LlmModelOpenAI : "gpt-5.5";
                }
                else if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
                {
                    targetModel = !string.IsNullOrEmpty(settings.LlmModelOpenRouter) ? settings.LlmModelOpenRouter : "meta-llama/llama-3.3-70b-instruct:free";
                }
                else if (provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
                {
                    targetModel = !string.IsNullOrEmpty(settings.LlmModelLmStudio) ? settings.LlmModelLmStudio : "";
                }

                PopulateModelChoices(provider, targetModel);
                UpdateModelRefreshButtonVisibility();

                if (provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase) || provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
                {
                    _ = RefreshLmStudioModelsAsync();
                }

                llmApiKeyBox.Password = await _llmService.GetApiKeyAsync(provider);
            };

            refreshLmStudioModelsButton.Click += async (_, __) => await RefreshLmStudioModelsAsync();

            var appearanceSection = CreateSection();
            AddLabel(appearanceSection, getString("SettingsLanguage", "애플리케이션 언어 (Language)"));
            appearanceSection.Children.Add(languageCombo);
            AddLabel(appearanceSection, getString("SettingsTheme", "앱/에디터 테마"));
            appearanceSection.Children.Add(themeCombo);
            AddLabel(appearanceSection, getString("SettingsUiFontFamily", "UI 쉘 폰트"));
            appearanceSection.Children.Add(uiFontFamilyCombo);
            AddLabel(appearanceSection, getString("SettingsFontFamily", "에디터 폰트"));
            appearanceSection.Children.Add(fontFamilyCombo);
            var fontSizeLabel = new TextBlock { Text = getString("SettingsFontSize", "에디터 글자 크기") + $" ({settings.FontSize:0}pt)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            appearanceSection.Children.Add(fontSizeLabel);
            appearanceSection.Children.Add(sizeSlider);
            sizeSlider.ValueChanged += (_, args) => fontSizeLabel.Text = getString("SettingsFontSize", "에디터 글자 크기") + $" ({args.NewValue:0}pt)";
            appearanceSection.Children.Add(customBgCheck);
            appearanceSection.Children.Add(customBgDropdown);
            appearanceSection.Children.Add(customFgCheck);
            appearanceSection.Children.Add(customFgDropdown);
            AddLabel(appearanceSection, getString("SettingsPreviewFontFamily", "프리뷰 폰트"));
            appearanceSection.Children.Add(previewFontFamilyCombo);
            var previewSizeLabel = new TextBlock { Text = getString("SettingsPreviewFontSize", "프리뷰 글자 크기") + $" ({settings.PreviewFontSize:0}pt)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            appearanceSection.Children.Add(previewSizeLabel);
            appearanceSection.Children.Add(previewSizeSlider);
            previewSizeSlider.ValueChanged += (_, args) => previewSizeLabel.Text = getString("SettingsPreviewFontSize", "프리뷰 글자 크기") + $" ({args.NewValue:0}pt)";
            appearanceSection.Children.Add(previewBgCheck);
            appearanceSection.Children.Add(previewBgDropdown);
            appearanceSection.Children.Add(previewFgCheck);
            appearanceSection.Children.Add(previewFgDropdown);

            var editorSection = CreateSection();
            editorSection.Children.Add(wordWrapCheck);
            editorSection.Children.Add(bracketColorCheck);
            editorSection.Children.Add(autocompleteEnterCheck);
            editorSection.Children.Add(autocompleteTabCheck);
            editorSection.Children.Add(autoSaveCheck);
            editorSection.Children.Add(defaultMarkdownCheck);
            editorSection.Children.Add(defaultMarkdownToolbarCheck);
            AddLabel(editorSection, getString("SettingsTabSize", "Tab size"));
            editorSection.Children.Add(tabSizeBox);

            var terminalSection = CreateSection();
            AddLabel(terminalSection, getString("SettingsTerminalProfile", "터미널 셸"));
            terminalSection.Children.Add(terminalProfileCombo);
            AddLabel(terminalSection, getString("SettingsTerminalFontFamily", "터미널 폰트"));
            terminalSection.Children.Add(terminalFontFamilyCombo);
            var terminalSizeLabel = new TextBlock { Text = getString("SettingsTerminalFontSize", "터미널 글자 크기") + $" ({settings.TerminalFontSize:0}pt)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            terminalSection.Children.Add(terminalSizeLabel);
            terminalSection.Children.Add(terminalSizeSlider);
            terminalSizeSlider.ValueChanged += (_, args) => terminalSizeLabel.Text = getString("SettingsTerminalFontSize", "터미널 글자 크기") + $" ({args.NewValue:0}pt)";
            terminalSection.Children.Add(new TextBlock
            {
                Text = getString("SettingsTerminalProfileInfo", "PowerShell은 PowerShell 7(pwsh)이 설치되어 있으면 자동으로 PowerShell 7을 사용합니다. CMD, Git Bash, WSL도 선택할 수 있으며 새 설정은 다음 새 터미널부터 적용됩니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            });

            var llmSection = CreateSection();
            AddLabel(llmSection, getString("SettingsLlmProvider", "LLM 공급자"));
            llmSection.Children.Add(llmProviderCombo);
            AddLabel(llmSection, getString("SettingsLlmEndpoint", "LLM API Endpoint"));
            llmSection.Children.Add(llmEndpointBox);

            AddLabel(llmSection, getString("SettingsLlmApiKey", "LLM API Key"));
            llmSection.Children.Add(llmApiKeyBox);
            llmSection.Children.Add(new TextBlock
            {
                Text = getString("SettingsLlmApiKeyInfo", "API Key는 설정 파일에 저장하지 않고 Windows 자격 증명 관리자에 저장합니다."),
                TextWrapping = TextWrapping.Wrap
            });

            AddLabel(llmSection, getString("SettingsLlmModel", "LLM 모델명"));
            llmSection.Children.Add(llmModelCombo);
            llmSection.Children.Add(refreshLmStudioModelsButton);
            llmSection.Children.Add(llmModelStatusText);

            llmSection.Children.Add(confirmBeforeSendingCheck);

            AddLabel(llmSection, getString("SettingsLlmSourceLanguage", "번역 원본 언어 (Source Language)"));
            llmSection.Children.Add(sourceLangCombo);
            AddLabel(llmSection, getString("SettingsLlmTargetLanguage", "번역 대상 언어 (Target Language)"));
            llmSection.Children.Add(targetLangCombo);

            var settingsPivot = new Pivot { Width = 500, Height = 440, FontSize = 12 };
            var toolbarSection = CreateSection();

            var topPanel = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var showLabelsCheck = new CheckBox { Content = getString("SettingsToolbarShowLabels", "툴바 버튼 글자 표시"), IsChecked = settings.ToolbarShowLabels };
            Grid.SetColumn(showLabelsCheck, 0);
            topPanel.Children.Add(showLabelsCheck);

            toolbarSection.Children.Add(topPanel);

            var visibilityHeader = new TextBlock
            {
                Text = getString("SettingsToolbarButtonVisibility", "툴바 버튼 표시/숨기기"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 2)
            };
            toolbarSection.Children.Add(visibilityHeader);

            var toolbarOptions = ToolbarButtonCatalog.All.ToList();
            var hiddenSet = new HashSet<string>(
                (settings.ToolbarHiddenButtons ?? new List<string>())
                    .Select(ToolbarButtonCatalog.NormalizeId),
                StringComparer.OrdinalIgnoreCase);
            var visibilityChecks = new List<CheckBox>();
            foreach (var option in toolbarOptions.Where(option => !option.IsRequired))
            {
                string label = getString(option.ResourceKey, option.Id);
                var chk = new CheckBox
                {
                    Content = label,
                    Tag = option.Id,
                    IsChecked = !hiddenSet.Contains(option.Id),
                    Margin = new Thickness(12, 0, 0, 0)
                };
                visibilityChecks.Add(chk);
                toolbarSection.Children.Add(chk);
            }
            var settingsNote = new TextBlock
            {
                Text = getString("SettingsToolbarSettingsPinned", "설정 버튼은 항상 표시됩니다."),
                FontSize = 11,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                Margin = new Thickness(12, 0, 0, 4)
            };
            toolbarSection.Children.Add(settingsNote);

            var reorderDesc = new TextBlock
            {
                Text = getString("SettingsToolbarDragHint", "드래그하여 버튼 순서 변경 (설정 버튼은 고정)"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 4)
            };
            toolbarSection.Children.Add(reorderDesc);
            var defaultOrder = NormalizeToolbarOrder(settings.ToolbarButtonOrder);
            var orderItems = new ObservableCollection<ToolbarOrderItem>(defaultOrder
                .Select(id => toolbarOptions.First(option => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Select(option => new ToolbarOrderItem(option.Id, getString(option.ResourceKey, option.Id))));
            var orderList = new ListView
            {
                Height = 240,
                SelectionMode = ListViewSelectionMode.None,
                AllowDrop = true,
                CanReorderItems = true,
                ItemsSource = orderItems
            };

            orderList.ItemTemplate = (Microsoft.UI.Xaml.DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <TextBlock Text=""{Binding Label}"" FontSize=""11"" Height=""18"" VerticalAlignment=""Center""/>
                  </DataTemplate>"
            );

            orderList.ItemContainerStyle = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                @"<Style TargetType=""ListViewItem"" xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <Setter Property=""MinHeight"" Value=""22""/>
                    <Setter Property=""Height"" Value=""22""/>
                    <Setter Property=""Padding"" Value=""8,1,8,1""/>
                  </Style>"
            );

            toolbarSection.Children.Add(orderList);

            var resetToolbarButton = new Button
            {
                Content = getString("SettingsToolbarReset", "툴바 초기화"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(resetToolbarButton, 1);
            topPanel.Children.Add(resetToolbarButton);

            resetToolbarButton.Click += (_, __) =>
            {
                showLabelsCheck.IsChecked = true;
                foreach (var chk in visibilityChecks)
                {
                    chk.IsChecked = true;
                }

                orderItems.Clear();
                var originalDefaultOrder = NormalizeToolbarOrder(null);
                foreach (var id in originalDefaultOrder)
                {
                    var opt = toolbarOptions.First(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    orderItems.Add(new ToolbarOrderItem(opt.Id, getString(opt.ResourceKey, opt.Id)));
                }
            };

            var shortcutsSection = CreateSection();
            shortcutsSection.Spacing = 4;
            shortcutsSection.Padding = new Thickness(12, 12, 12, 12);

            var headerKey = new TextBlock { Text = getString("SettingsShortcutsHeaderKey", "단축키"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 11 };
            var headerDesc = new TextBlock { Text = getString("SettingsShortcutsHeaderDesc", "설명"), FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 11 };

            var headerRow = new Grid { Padding = new Thickness(6, 6, 6, 6), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)), CornerRadius = new CornerRadius(4) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(headerKey, 0);
            Grid.SetColumn(headerDesc, 1);
            headerRow.Children.Add(headerKey);
            headerRow.Children.Add(headerDesc);
            shortcutsSection.Children.Add(headerRow);

            var shortcutList = new List<(string Key, string Desc)>
            {
                ("Ctrl + N", getString("ShortcutDescNewTab", "새 편집 탭을 엽니다.")),
                ("Ctrl + S", getString("ShortcutDescSave", "현재 파일을 저장합니다.")),
                ("Ctrl + Shift + S", getString("ShortcutDescSaveAs", "다른 이름으로 저장 대화상자를 엽니다.")),
                ("Ctrl + O", getString("ShortcutDescOpen", "파일 열기 대화상자를 엽니다.")),
                ("Ctrl + F", getString("ShortcutDescFind", "에디터 내 검색 창을 활성화합니다.")),
                ("Ctrl + W", getString("ShortcutDescClose", "현재 탭을 닫습니다.")),
                ("Ctrl + P", getString("ShortcutDescPrint", "현재 문서를 인쇄합니다.")),
                ("Ctrl + 1", getString("ShortcutDescLeftPanel", "좌측 패널(탐색기, 검색, 북마크 등)을 토글합니다.")),
                ("Ctrl + 2", getString("ShortcutDescRightPanel", "우측 패널(실시간 미리보기, AI Assistant 등)을 토글합니다.")),
                ("Ctrl + `", getString("ShortcutDescTerminal", "내장 터미널을 토글합니다.")),
                ("Ctrl + Z", getString("ShortcutDescUndo", "이전 작업을 실행 취소합니다.")),
                ("Ctrl + Y", getString("ShortcutDescRedo", "실행 취소한 작업을 다시 실행합니다.")),
                ("Ctrl + C", getString("ShortcutDescCopy", "선택 영역을 복사합니다.")),
                ("Ctrl + V", getString("ShortcutDescPaste", "클립보드 내용을 붙여넣습니다.")),
                ("Ctrl + X", getString("ShortcutDescCut", "선택 영역을 잘라냅니다.")),
                ("Ctrl + Enter", getString("ShortcutDescAiPrompt", "AI 질문 입력창에서 프롬프트를 전송합니다.")),
                ("F9", getString("ShortcutDescTopMost", "항상위 토글")),
                ("F10", getString("ShortcutDescTheme", "테마 토글")),
                ("F12", getString("ShortcutDescStickyNote", "스티커 노트"))
            };

            bool alternate = false;
            foreach (var item in shortcutList)
            {
                var rowGrid = new Grid { Padding = new Thickness(6, 6, 6, 6) };
                if (alternate)
                {
                    rowGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(10, 128, 128, 128));
                }
                alternate = !alternate;

                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var shortcutKeyBorder = new Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(15, 128, 128, 128)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var shortcutKeyText = new TextBlock
                {
                    Text = item.Key,
                    FontSize = 10.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
                };
                shortcutKeyBorder.Child = shortcutKeyText;

                var shortcutDescText = new TextBlock
                {
                    Text = item.Desc,
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };

                Grid.SetColumn(shortcutKeyBorder, 0);
                Grid.SetColumn(shortcutDescText, 1);

                rowGrid.Children.Add(shortcutKeyBorder);
                rowGrid.Children.Add(shortcutDescText);

                var rowContainer = new StackPanel();
                rowContainer.Children.Add(rowGrid);
                rowContainer.Children.Add(new Border
                {
                    Height = 1,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(15, 128, 128, 128)),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                });

                shortcutsSection.Children.Add(rowContainer);
            }

            var aboutSection = CreateSection();
            aboutSection.HorizontalAlignment = HorizontalAlignment.Stretch;
            aboutSection.Spacing = 6;
            aboutSection.Padding = new Thickness(16, 8, 16, 12);

            var iconImage = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/TxtAIEditor.png")),
                Width = 72,
                Height = 72,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 2)
            };
            aboutSection.Children.Add(iconImage);

            string appVersion = GetAppVersion();
            var titleText = new TextBlock
            {
                Text = $"TxtAIEditor (v{appVersion})",
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };
            aboutSection.Children.Add(titleText);

            var descText = new TextBlock
            {
                Text = getString("SettingsAboutDescription", "강력하고 가벼운 텍스트 및 마크다운 에디터입니다.\n실시간 미리보기, 코드 및 수식 템플릿, 터미널 인터페이스, Git 통합, AI Assistant 등을 지원합니다."),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                Margin = new Thickness(0, 0, 0, 4)
            };
            aboutSection.Children.Add(descText);

            var separator = new Border
            {
                Height = 1,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(20, 2, 20, 4)
            };
            aboutSection.Children.Add(separator);

            var githubHeader = new TextBlock
            {
                Text = getString("SettingsAboutProjectGitHub", "Project GitHub"),
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            aboutSection.Children.Add(githubHeader);

            var githubLink = new HyperlinkButton
            {
                Content = "https://github.com/kirinonakar/TxtAIEditor",
                NavigateUri = new Uri("https://github.com/kirinonakar/TxtAIEditor"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 0, 2)
            };
            aboutSection.Children.Add(githubLink);

            var thirdPartyNoticesButton = new Button
            {
                Content = getString("SettingsAboutThirdPartyNotices", "오픈소스 라이선스 고지 (Third-Party Notices)"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };

            var noticesFlyout = new Flyout
            {
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = @"This software includes Mermaid, xterm.js, and KaTeX.

Mermaid
License: MIT License
Copyright (c) 2014 - 2022 Knut Sveidqvist

xterm.js
License: MIT License
Copyright (c) 2017-2019, The xterm.js authors
Copyright (c) 2014-2016, SourceLair Private Company
Copyright (c) 2012-2013, Christopher Jeffrey

KaTeX
License: MIT License
Copyright (c) 2013-2020 Khan Academy and other contributors

The MIT License (MIT)

The MIT license text below applies to the third-party components listed above.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 10.5,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
                    },
                    MaxHeight = 280,
                    Width = 380
                }
            };
            thirdPartyNoticesButton.Flyout = noticesFlyout;
            aboutSection.Children.Add(thirdPartyNoticesButton);

            var copyrightText = new TextBlock
            {
                Text = "Copyright © 2026 kirinonakar. All rights reserved.",
                FontSize = 10,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            aboutSection.Children.Add(copyrightText);

            settingsPivot.Items.Add(new PivotItem { Header = new TextBlock { Text = getString("SettingsAppearance", "모양"), FontSize = 13 }, Content = new ScrollViewer { Content = appearanceSection } });
            settingsPivot.Items.Add(new PivotItem { Header = new TextBlock { Text = getString("SettingsEditing", "편집"), FontSize = 13 }, Content = new ScrollViewer { Content = editorSection } });
            settingsPivot.Items.Add(new PivotItem { Header = new TextBlock { Text = getString("SettingsTerminal", "터미널"), FontSize = 13 }, Content = new ScrollViewer { Content = terminalSection } });
            settingsPivot.Items.Add(new PivotItem { Header = new TextBlock { Text = getString("SettingsToolbarCustomization", "툴바"), FontSize = 13 }, Content = new ScrollViewer { Content = toolbarSection } });
            settingsPivot.Items.Add(new PivotItem { Header = new TextBlock { Text = getString("SettingsLLM", "LLM"), FontSize = 13 }, Content = new ScrollViewer { Content = llmSection } });
            settingsPivot.Items.Add(new PivotItem { Header = new TextBlock { Text = getString("SettingsShortcuts", "단축키"), FontSize = 13 }, Content = new ScrollViewer { Content = shortcutsSection } });
            settingsPivot.Items.Add(new PivotItem { Header = new TextBlock { Text = getString("SettingsAbout", "정보"), FontSize = 13 }, Content = new ScrollViewer { Content = aboutSection } });

            ApplyCompactStyleToLogicalTree(settingsPivot);

            // Re-apply custom font sizes for About tab to prevent them being flattened by compact style
            titleText.FontSize = 17;
            descText.FontSize = 10.5;
            copyrightText.FontSize = 9.5;

            bool isDarkTheme = xamlRoot.Content is FrameworkElement fe && fe.ActualTheme == ElementTheme.Dark;

            var dialog = new ContentDialog
            {
                Title = getString("SettingsTitle", "TxtAIEditor 설정"),
                Content = settingsPivot,
                PrimaryButtonText = getString("SettingsSave", "적용 및 저장"),
                CloseButtonText = getString("SettingsCancel", "취소"),
                XamlRoot = xamlRoot,
                RequestedTheme = isDarkTheme ? ElementTheme.Dark : ElementTheme.Light
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return new SettingsDialogResult { Saved = false };
            }

            settings.Language = languageCombo.SelectedIndex switch
            {
                1 => "ko-KR",
                2 => "en-US",
                3 => "ja-JP",
                _ => "Default"
            };

            settings.Theme = themeCombo.SelectedIndex == 0 ? "Dark" : "Light";
            settings.FontSize = sizeSlider.Value;
            settings.CustomBackgroundColor = customBgCheck.IsChecked == true ? ColorToHex(customBgPicker.Color) : string.Empty;
            settings.CustomForegroundColor = customFgCheck.IsChecked == true ? ColorToHex(customFgPicker.Color) : string.Empty;
            settings.FontFamily = GetSelectedComboText(fontFamilyCombo, settings.FontFamily);
            settings.UiFontFamily = GetSelectedComboText(uiFontFamilyCombo, settings.UiFontFamily);
            settings.TerminalFontFamily = GetSelectedComboText(terminalFontFamilyCombo, settings.TerminalFontFamily);
            settings.TerminalFontSize = terminalSizeSlider.Value;
            settings.PreviewFontFamily = GetSelectedComboText(previewFontFamilyCombo, settings.PreviewFontFamily);
            settings.PreviewFontSize = previewSizeSlider.Value;
            settings.PreviewCustomBackgroundColor = previewBgCheck.IsChecked == true ? ColorToHex(previewBgPicker.Color) : string.Empty;
            settings.PreviewCustomForegroundColor = previewFgCheck.IsChecked == true ? ColorToHex(previewFgPicker.Color) : string.Empty;
            settings.WordWrap = wordWrapCheck.IsChecked == true;
            settings.BracketPairColorization = bracketColorCheck.IsChecked == true;
            settings.AutocompleteOnEnter = autocompleteEnterCheck.IsChecked == true;
            settings.AutocompleteOnTab = autocompleteTabCheck.IsChecked == true;
            settings.AutoSave = autoSaveCheck.IsChecked == true;
            if (int.TryParse(tabSizeBox.Text.Trim(), out int tabSize))
            {
                settings.TabSize = Math.Clamp(tabSize, 1, 16);
            }
            settings.TerminalProfile = (terminalProfileCombo.SelectedItem as TerminalProfileChoice)?.Id ?? "PowerShell";

            settings.LlmProvider = GetSelectedProviderName();
            settings.LlmEndpoint = llmEndpointBox.Text.Trim();
            string selectedModelText = llmModelCombo.Text?.Trim() ?? string.Empty;
            settings.LlmModel = (!string.IsNullOrEmpty(selectedModelText) ? selectedModelText : (llmModelCombo.SelectedItem as string ?? settings.LlmModel)).Trim();
            settings.LlmConfirmBeforeSending = confirmBeforeSendingCheck.IsChecked == true;

            settings.LlmSourceLanguage = sourceLangCombo.SelectedIndex switch
            {
                1 => "Korean",
                2 => "English",
                3 => "Japanese",
                4 => "Chinese",
                5 => "French",
                6 => "Spanish",
                7 => "German",
                _ => "Auto"
            };

            settings.LlmTargetLanguage = targetLangCombo.SelectedIndex switch
            {
                1 => "English",
                2 => "Japanese",
                3 => "Chinese",
                4 => "French",
                5 => "Spanish",
                6 => "German",
                _ => "Korean"
            };

            if (settings.LlmProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelGemini = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelOpenAI = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelOpenRouter = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelLmStudio = settings.LlmModel;
            }

            settings.DefaultMarkdownEnabled = defaultMarkdownCheck.IsChecked == true;
            settings.RightSidebarVisible = settings.DefaultMarkdownEnabled;
            settings.DefaultMarkdownToolbarEnabled = defaultMarkdownToolbarCheck.IsChecked == true;

            settings.ToolbarShowLabels = showLabelsCheck.IsChecked == true;
            settings.ToolbarButtonOrder = (orderList.ItemsSource as ObservableCollection<ToolbarOrderItem>)?
                .Select(item => item.Id)
                .ToList()
                ?? ToolbarButtonCatalog.DefaultOrder.ToList();
            settings.ToolbarHiddenButtons = visibilityChecks
                .Where(check => check.IsChecked == false)
                .Select(check => check.Tag as string ?? string.Empty)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            string newApiKey = llmApiKeyBox.Password.Trim();
            await _llmService.SaveApiKeyAsync(settings.LlmProvider, newApiKey);
            string apiKeyStatus = string.IsNullOrEmpty(newApiKey)
                ? $"{settings.LlmProvider} API Key가 Windows 자격 증명 저장소에서 삭제되었습니다."
                : $"{settings.LlmProvider} API Key가 Windows 자격 증명 저장소에 저장되었습니다.";

            return new SettingsDialogResult
            {
                Saved = true,
                ApiKeyStatusMessage = apiKeyStatus
            };
        }

        private static StackPanel CreateSection()
        {
            return new StackPanel { Spacing = 6, Width = 460, Padding = new Thickness(2, 6, 2, 2) };
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

        private static void AddLabel(StackPanel target, string text)
        {
            target.Children.Add(new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        }

        private static ComboBox CreateFontComboBox(string currentFontFamily, IReadOnlyList<string> fontFamilies)
        {
            var comboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "폰트 선택"
            };

            string current = string.IsNullOrWhiteSpace(currentFontFamily)
                ? "Consolas"
                : currentFontFamily.Trim();

            if (!fontFamilies.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                comboBox.Items.Add(current);
            }

            foreach (string family in fontFamilies)
            {
                comboBox.Items.Add(family);
            }

            comboBox.SelectedItem = comboBox.Items
                .OfType<string>()
                .FirstOrDefault(item => item.Equals(current, StringComparison.OrdinalIgnoreCase))
                ?? comboBox.Items.OfType<string>().FirstOrDefault();

            return comboBox;
        }

        private static string GetSelectedComboText(ComboBox comboBox, string fallback)
        {
            return (comboBox.SelectedItem as string)?.Trim() ?? fallback.Trim();
        }

        private static DropDownButton CreateColorDropdown(string title, Windows.UI.Color initialColor, out ColorPicker colorPicker)
        {
            var swatch = new Border
            {
                Width = 120,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(120, 128, 128, 128)),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(initialColor)
            };

            var picker = new ColorPicker
            {
                Color = initialColor,
                IsAlphaEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsMoreButtonVisible = false
            };
            colorPicker = picker;

            var flyoutContent = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 6,
                Padding = new Thickness(6)
            };
            flyoutContent.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            flyoutContent.Children.Add(picker);

            ApplyCompactStyleToLogicalTree(flyoutContent);

            picker.Loaded += (s, e) =>
            {
                ApplyCompactStyleToVisualTree(picker);
            };

            picker.ColorChanged += (_, __) =>
            {
                swatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(picker.Color);
            };

            var flyoutStyle = new Microsoft.UI.Xaml.Style(typeof(Microsoft.UI.Xaml.Controls.FlyoutPresenter));
            flyoutStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(Microsoft.UI.Xaml.Controls.Control.PaddingProperty, new Thickness(8)));
            flyoutStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(Microsoft.UI.Xaml.Controls.Control.MinWidthProperty, 360.0));
            flyoutStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(Microsoft.UI.Xaml.Controls.Control.MaxWidthProperty, 400.0));

            return new DropDownButton
            {
                Content = swatch,
                Flyout = new Flyout 
                { 
                    Content = flyoutContent,
                    FlyoutPresenterStyle = flyoutStyle
                },
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        private static IReadOnlyList<string> GetInstalledFontFamilies()
        {
            if (_installedFontFamiliesCache != null)
            {
                return _installedFontFamiliesCache;
            }

            var fonts = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase)
            {
                "Consolas",
                "Courier New",
                "Segoe UI",
                "Malgun Gothic"
            };

            AddFontsFromRegistry(fonts, Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"));
            AddFontsFromRegistry(fonts, Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"));

            _installedFontFamiliesCache = fonts.ToList();
            return _installedFontFamiliesCache;
        }

        private static void AddFontsFromRegistry(ISet<string> fonts, Microsoft.Win32.RegistryKey? key)
        {
            if (key == null)
            {
                return;
            }

            using (key)
            {
                foreach (string valueName in key.GetValueNames())
                {
                    string family = NormalizeFontRegistryName(valueName);
                    if (!string.IsNullOrWhiteSpace(family))
                    {
                        fonts.Add(family);
                    }
                }
            }
        }

        private static string NormalizeFontRegistryName(string valueName)
        {
            string family = Regex.Replace(valueName, @"\s*\([^)]+\)\s*$", string.Empty).Trim();
            family = Regex.Replace(family, @"\s+(Regular|Normal|Bold|Italic|Oblique|Light|Medium|SemiBold|Semibold|ExtraLight|ExtraBold|Black|Thin|Condensed|Narrow)$", string.Empty, RegexOptions.IgnoreCase).Trim();
            return family;
        }

        private static Windows.UI.Color ResolvePickerColor(string? colorValue, string fallbackHex)
        {
            if (TryParseHexColor(colorValue, out var color) || TryParseHexColor(fallbackHex, out color))
            {
                return color;
            }

            return Windows.UI.Color.FromArgb(255, 0, 0, 0);
        }

        private static string ColorToHex(Windows.UI.Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private static bool TryParseHexColor(string? value, out Windows.UI.Color color)
        {
            color = Windows.UI.Color.FromArgb(255, 0, 0, 0);
            string hex = (value ?? string.Empty).Trim().TrimStart('#');
            if (hex.Length != 6)
            {
                return false;
            }

            try
            {
                byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
                color = Windows.UI.Color.FromArgb(255, r, g, b);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<IReadOnlyList<string>> FetchLmStudioModelsAsync(string endpoint)
        {
            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:1234/v1" : endpoint.Trim();
            string requestUrl = baseEndpoint.TrimEnd('/') + "/models";

            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
            using (var response = await client.GetAsync(requestUrl))
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"모델 목록 요청 실패 ({response.StatusCode}): {responseBody}");
                }

                using (var doc = JsonDocument.Parse(responseBody))
                {
                    var models = new List<string>();
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idElement))
                            {
                                string? id = idElement.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    models.Add(id);
                                }
                            }
                        }
                    }

                    return models;
                }
            }
        }

        private static void ApplyCompactStyleToLogicalTree(object element)
        {
            if (element == null) return;

            if (element is Control ctrl)
            {
                if (ctrl is not Pivot && ctrl is not PivotItem)
                {
                    if (ctrl is ListView)
                    {
                        ctrl.FontSize = 11;
                    }
                    else if (ctrl.Tag as string == "LlmModelCombo")
                    {
                        ctrl.FontSize = 11;
                    }
                    else
                    {
                        ctrl.FontSize = 11.5;
                    }
                }

                if (ctrl is DropDownButton ddb)
                {
                    ddb.MinHeight = 26;
                    ddb.Height = Double.NaN; // Allow automatic height so swatch is not clipped
                    ddb.Padding = new Thickness(6, 2, 6, 2);
                    ddb.VerticalAlignment = VerticalAlignment.Center;
                }
                else if (ctrl is ComboBox || ctrl is TextBox || ctrl is PasswordBox || ctrl is Button)
                {
                    ctrl.MinHeight = 26;
                    ctrl.Height = 26;
                    if (ctrl.Tag as string == "LlmModelCombo")
                    {
                        ctrl.Padding = new Thickness(4, 1, 4, 1);
                    }
                    else
                    {
                        ctrl.Padding = new Thickness(8, 2, 8, 2);
                    }
                    ctrl.VerticalAlignment = VerticalAlignment.Center;
                }
                else if (ctrl is CheckBox chk)
                {
                    chk.MinHeight = 22;
                    chk.Padding = new Thickness(8, 2, 0, 2);
                    chk.Margin = new Thickness(chk.Margin.Left, 1, chk.Margin.Right, 1);
                }
            }
            else if (element is TextBlock tb)
            {
                if (tb.FontSize != 11 && tb.FontSize != 12)
                {
                    tb.FontSize = 11.5;
                }
            }

            if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    ApplyCompactStyleToLogicalTree(child);
                }
            }
            else if (element is ContentControl cc)
            {
                ApplyCompactStyleToLogicalTree(cc.Content);
            }
            else if (element is ScrollViewer sv)
            {
                ApplyCompactStyleToLogicalTree(sv.Content);
            }
            else if (element is Pivot pivot)
            {
                foreach (var item in pivot.Items)
                {
                    ApplyCompactStyleToLogicalTree(item);
                }
            }
        }

        private static void ApplyCompactStyleToVisualTree(Microsoft.UI.Xaml.DependencyObject element)
        {
            if (element == null) return;

            int childrenCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i);
                if (child is Control ctrl)
                {
                    ctrl.FontSize = 10.5;

                    if (ctrl is TextBox || ctrl is ComboBox || ctrl is Button)
                    {
                        ctrl.MinHeight = 22;
                        ctrl.Height = 22;
                        ctrl.Padding = new Thickness(4, 1, 4, 1);
                    }
                }
                else if (child is TextBlock tb)
                {
                    tb.FontSize = 10.5;
                }

                ApplyCompactStyleToVisualTree(child);
            }
        }

        private static void ApplyEditableComboBoxVisualStyles(Microsoft.UI.Xaml.DependencyObject parent)
        {
            if (parent == null) return;

            int childrenCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox textBox)
                {
                    textBox.FontSize = 11;
                    textBox.MinHeight = 24;
                    textBox.Height = 24;
                    textBox.Padding = new Thickness(4, 1, 4, 1);
                    textBox.VerticalAlignment = VerticalAlignment.Center;
                    textBox.IsSpellCheckEnabled = false;
                    textBox.IsTextPredictionEnabled = false;

                    textBox.GotFocus += (s, e) =>
                    {
                        textBox.FontSize = 11;
                        textBox.MinHeight = 24;
                        textBox.Height = 24;
                        textBox.Padding = new Thickness(4, 1, 4, 1);
                        textBox.IsSpellCheckEnabled = false;
                        textBox.IsTextPredictionEnabled = false;
                    };
                }
                else
                {
                    ApplyEditableComboBoxVisualStyles(child);
                }
            }
        }

        private static string GetAppVersion()
        {
            try
            {
                // Try getting it from the packaged identity if running under package context
                try
                {
                    var package = Windows.ApplicationModel.Package.Current;
                    var version = package.Id.Version;
                    return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
                }
                catch (System.InvalidOperationException)
                {
                    // Not running in a packaged context
                }

                // Search for Package.appxmanifest in the directory tree starting from AppContext.BaseDirectory
                string dir = AppContext.BaseDirectory;
                while (!string.IsNullOrEmpty(dir))
                {
                    string manifestPath = System.IO.Path.Combine(dir, "Package.appxmanifest");
                    if (System.IO.File.Exists(manifestPath))
                    {
                        var doc = new System.Xml.XmlDocument();
                        doc.Load(manifestPath);
                        var nsmgr = new System.Xml.XmlNamespaceManager(doc.NameTable);
                        nsmgr.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
                        var identityNode = doc.SelectSingleNode("//f:Identity", nsmgr) ?? doc.SelectSingleNode("//Identity");
                        if (identityNode is System.Xml.XmlElement element)
                        {
                            string version = element.GetAttribute("Version");
                            if (!string.IsNullOrEmpty(version))
                            {
                                return version;
                            }
                        }
                    }
                    string? parent = System.IO.Path.GetDirectoryName(dir);
                    if (parent == dir || string.IsNullOrEmpty(parent))
                    {
                        break;
                    }
                    dir = parent;
                }
            }
            catch
            {
                // Fallback
            }

            try
            {
                var assemblyVersion = typeof(SettingsDialogService).Assembly.GetName().Version;
                if (assemblyVersion != null)
                {
                    return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}.{assemblyVersion.Revision}";
                }
            }
            catch
            {
                // Fallback
            }

            return "1.0.1.0";
        }
    }
}
