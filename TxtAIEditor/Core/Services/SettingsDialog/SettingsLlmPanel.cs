using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsLlmPanel : UserControl
    {
        private readonly EditorSettings _settings;
        private readonly ILLMService _llmService;
        private readonly Func<string, string, string> _getString;
        private readonly ComboBox _llmProviderCombo;
        private readonly TextBox _llmEndpointBox;
        private readonly ComboBox _llmModelCombo;
        private readonly PasswordBox _llmApiKeyBox;
        private readonly TextBox _exaEndpointBox;
        private readonly PasswordBox _exaApiKeyBox;
        private readonly CheckBox _confirmBeforeSendingCheck;
        private readonly CheckBox _agentVerboseCheck;
        private readonly CheckBox _agentAutoApproveGitEditsCheck;
        private readonly CheckBox _agentAutoApprovePlanningCheck;
        private readonly ComboBox _sourceLangCombo;
        private readonly ComboBox _targetLangCombo;
        private readonly ComboBox _llmThinkingLevelCombo;
        private readonly Button _refreshModelsButton;
        private readonly TextBlock _modelStatusText;
        private readonly DropDownButton _tokenUsageStatsButton;
        private readonly TextBlock _tokenUsageSummaryText;
        private readonly TextBlock _tokenUsageDetailText;
        private readonly Button _resetTokenUsageStatsButton;
        private readonly NumberBox _maxToolCallsBox;

        private SettingsLlmPanel(EditorSettings settings, ILLMService llmService, Func<string, string, string> getString)
        {
            _settings = settings;
            _llmService = llmService;
            _getString = getString;

            _llmProviderCombo = CreateProviderCombo(settings);
            _llmEndpointBox = new TextBox { PlaceholderText = getString("SettingsLlmEndpointPlaceholder", "예: http://localhost:1234/v1"), Text = settings.LlmEndpoint, HorizontalAlignment = HorizontalAlignment.Stretch };
            _llmModelCombo = new ComboBox { PlaceholderText = getString("SettingsLlmSelectModel", "모델 선택"), HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = true, Tag = "LlmModelCombo" };
            _llmModelCombo.Loaded += (_, __) => SettingsDialogStyler.ApplyEditableComboBoxVisualStyles(_llmModelCombo);
            _llmApiKeyBox = new PasswordBox { PasswordChar = "●", PlaceholderText = getString("SettingsLlmCredentialPlaceholder", "API Key 입력 (비워두면 저장된 자격 증명 삭제)"), HorizontalAlignment = HorizontalAlignment.Stretch };
            _exaEndpointBox = new TextBox { PlaceholderText = getString("SettingsExaEndpointPlaceholder", "예: https://mcp.exa.ai/mcp"), Text = settings.ExaEndpoint, HorizontalAlignment = HorizontalAlignment.Stretch };
            _exaApiKeyBox = new PasswordBox { PasswordChar = "●", PlaceholderText = getString("SettingsExaApiKeyPlaceholder", "Exa API Key 입력 (비워두면 저장된 Key 삭제)"), HorizontalAlignment = HorizontalAlignment.Stretch };

            _confirmBeforeSendingCheck = new CheckBox { Content = getString("SettingsLlmConfirmBeforeSending", "전송 전 확인"), IsChecked = settings.LlmConfirmBeforeSending };
            _agentVerboseCheck = new CheckBox { Content = getString("SettingsLlmAgentVerbose", "Agent 상세 출력 활성화 (Verbose)"), IsChecked = settings.LlmAgentVerbose };
            _agentAutoApproveGitEditsCheck = new CheckBox { Content = getString("SettingsLlmAgentAutoApproveGitEdits", "Git 폴더 내 파일 변경/생성 자동 승인"), IsChecked = settings.LlmAgentAutoApproveGitEdits };
            _agentAutoApprovePlanningCheck = new CheckBox { Content = getString("SettingsLlmAgentAutoApprovePlanning", "계획 실행 자동 승인"), IsChecked = settings.LlmAgentAutoApprovePlanning };
            _sourceLangCombo = CreateSourceLanguageCombo(settings, getString);
            _targetLangCombo = CreateTargetLanguageCombo(settings, getString);
            _llmThinkingLevelCombo = CreateThinkingLevelCombo(settings, getString);
            _refreshModelsButton = new Button { Content = getString("SettingsLlmLoadModels", "LM Studio 모델 불러오기"), HorizontalAlignment = HorizontalAlignment.Stretch };
            _modelStatusText = new TextBlock
            {
                Text = getString("SettingsLlmInfo", "LM Studio는 서버가 켜져 있을 때 http://localhost:1234/v1/models 에서 모델 목록을 불러옵니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            _tokenUsageSummaryText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            _tokenUsageDetailText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            _resetTokenUsageStatsButton = new Button
            {
                Content = getString("SettingsLlmTokenUsageStatsReset", "통계 초기화"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _tokenUsageStatsButton = CreateTokenUsageStatsButton();
            _maxToolCallsBox = new NumberBox
            {
                Minimum = 0,
                Maximum = 500,
                Value = Math.Clamp(settings.LlmMaxToolCalls, 0, 500),
                SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            PopulateModelChoices(GetSelectedProviderName(), settings.LlmModel);
            UpdateModelRefreshButtonVisibility();
            RefreshTokenUsageStatsDisplay();
            AddEventHandlers();
            Content = CreateSection();
        }

        public static async Task<SettingsLlmPanel> CreateAsync(
            EditorSettings settings,
            ILLMService llmService,
            Func<string, string, string> getString)
        {
            var panel = new SettingsLlmPanel(settings, llmService, getString);
            await panel.LoadSecretsAsync();
            return panel;
        }

        public void ApplyToSettings(EditorSettings settings)
        {
            settings.LlmProvider = GetSelectedProviderName();
            settings.LlmEndpoint = _llmEndpointBox.Text.Trim();
            string selectedModelText = _llmModelCombo.Text?.Trim() ?? string.Empty;
            settings.LlmModel = (!string.IsNullOrEmpty(selectedModelText) ? selectedModelText : (_llmModelCombo.SelectedItem as string ?? settings.LlmModel)).Trim();
            settings.LlmConfirmBeforeSending = _confirmBeforeSendingCheck.IsChecked == true;
            settings.LlmAgentVerbose = _agentVerboseCheck.IsChecked == true;
            settings.LlmAgentAutoApproveGitEdits = _agentAutoApproveGitEditsCheck.IsChecked == true;
            settings.LlmAgentAutoApprovePlanning = _agentAutoApprovePlanningCheck.IsChecked == true;
            settings.LlmMaxToolCalls = (int)Math.Clamp(_maxToolCallsBox.Value, 0, 500);
            settings.LlmThinkingLevel = _llmThinkingLevelCombo.SelectedIndex switch
            {
                1 => "disabled",
                2 => "low",
                3 => "medium",
                4 => "high",
                5 => "xhigh",
                _ => "default"
            };
            settings.LlmSourceLanguage = _sourceLangCombo.SelectedIndex switch
            {
                1 => "Korean",
                2 => "English",
                3 => "Japanese",
                4 => "Chinese Simplified",
                5 => "Chinese Traditional",
                6 => "French",
                7 => "Spanish",
                8 => "German",
                _ => "Auto"
            };
            settings.LlmTargetLanguage = _targetLangCombo.SelectedIndex switch
            {
                1 => "Korean",
                2 => "English",
                3 => "Japanese",
                4 => "Chinese Simplified",
                5 => "Chinese Traditional",
                6 => "French",
                7 => "Spanish",
                8 => "German",
                _ => "Default"
            };
            SettingsLlmModelCatalog.SaveProviderModel(settings);
            settings.ExaEndpoint = _exaEndpointBox.Text.Trim();
        }

        public async Task SaveSecretsAsync(EditorSettings settings)
        {
            await _llmService.SaveApiKeyAsync(settings.LlmProvider, _llmApiKeyBox.Password.Trim());
            await _llmService.SaveApiKeyAsync("Exa", _exaApiKeyBox.Password.Trim());
        }

        public string CreateApiKeyStatusMessage(EditorSettings settings)
        {
            string newApiKey = _llmApiKeyBox.Password.Trim();
            string credentialLabel = settings.LlmProvider.Equals("OpenAI OAuth", StringComparison.OrdinalIgnoreCase)
                ? _getString("SettingsLlmOAuthAccessTokenName", "OAuth Access Token")
                : _getString("SettingsLlmApiKeyName", "API Key");
            string format = string.IsNullOrEmpty(newApiKey)
                ? _getString("SettingsLlmCredentialDeletedFormat", "{0} {1}가 Windows 자격 증명 저장소에서 삭제되었습니다.")
                : _getString("SettingsLlmCredentialSavedFormat", "{0} {1}가 Windows 자격 증명 저장소에 저장되었습니다.");
            return string.Format(format, settings.LlmProvider, credentialLabel);
        }

        private async Task LoadSecretsAsync()
        {
            _llmApiKeyBox.Password = await _llmService.GetApiKeyAsync(GetSelectedProviderName());
            _exaApiKeyBox.Password = await _llmService.GetApiKeyAsync("Exa");
        }

        private StackPanel CreateSection()
        {
            var section = SettingsDialogUi.CreateSection();
            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmProvider", "LLM 공급자"));
            section.Children.Add(_llmProviderCombo);
            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmEndpoint", "LLM API Endpoint"));
            section.Children.Add(_llmEndpointBox);

            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmCredential", "LLM API Key"));
            section.Children.Add(_llmApiKeyBox);
            section.Children.Add(new TextBlock
            {
                Text = _getString("SettingsLlmCredentialInfo", "API Key는 설정 파일에 저장하지 않고 Windows 자격 증명 관리자에 저장합니다."),
                TextWrapping = TextWrapping.Wrap
            });

            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmModel", "LLM 모델명"));
            section.Children.Add(_llmModelCombo);
            section.Children.Add(_refreshModelsButton);
            section.Children.Add(_modelStatusText);

            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmThinkingLevel", "Thinking Level"));
            section.Children.Add(_llmThinkingLevelCombo);
            SettingsDialogUi.AddLabel(section, _getString("SettingsExaEndpoint", "Exa 검색 API / MCP Endpoint"));
            section.Children.Add(_exaEndpointBox);
            SettingsDialogUi.AddLabel(section, _getString("SettingsExaApiKey", "Exa API Key (웹 검색 기능용)"));
            section.Children.Add(_exaApiKeyBox);
            section.Children.Add(new TextBlock
            {
                Text = _getString("SettingsExaApiKeyInfo", "Exa API Key는 Agent의 웹 검색(Exa) 기능에 사용되며, Windows 자격 증명 관리자에 안전하게 저장됩니다."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
            });

            section.Children.Add(_confirmBeforeSendingCheck);
            section.Children.Add(_agentVerboseCheck);
            section.Children.Add(_agentAutoApproveGitEditsCheck);
            section.Children.Add(_agentAutoApprovePlanningCheck);

            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmMaxToolCalls", "도구 호출 최대 횟수 (Max Tool Calls)"));
            section.Children.Add(_maxToolCallsBox);
            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmSourceLanguage", "번역 원본 언어 (Source Language)"));
            section.Children.Add(_sourceLangCombo);
            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmTargetLanguage", "번역 대상 언어 (Target Language)"));
            section.Children.Add(_targetLangCombo);

            SettingsDialogUi.AddLabel(section, _getString("SettingsLlmTokenUsageStatsLabel", "Cached token 통계"));
            section.Children.Add(_tokenUsageStatsButton);
            section.Children.Add(_tokenUsageSummaryText);
            return section;
        }

        private void AddEventHandlers()
        {
            _llmProviderCombo.SelectionChanged += async (_, __) =>
            {
                string provider = GetSelectedProviderName();
                ApplyProviderDefaults(provider);
                PopulateModelChoices(provider, SettingsLlmModelCatalog.GetModelForProviderChange(_settings, provider));
                UpdateModelRefreshButtonVisibility();

                if (SettingsLlmModelCatalog.SupportsRemoteModelFetch(provider))
                {
                    _ = RefreshModelsAsync();
                }

                _llmApiKeyBox.Password = await _llmService.GetApiKeyAsync(provider);
            };

            _refreshModelsButton.Click += async (_, __) => await RefreshModelsAsync();
            _tokenUsageStatsButton.Click += (_, __) => RefreshTokenUsageStatsDisplay();
            _resetTokenUsageStatsButton.Click += (_, __) =>
            {
                _llmService.ResetTokenUsageStats();
                RefreshTokenUsageStatsDisplay();
            };
        }

        private DropDownButton CreateTokenUsageStatsButton()
        {
            var flyoutContent = new StackPanel
            {
                Spacing = 8,
                Padding = new Thickness(8),
                MinWidth = 380
            };
            flyoutContent.Children.Add(_tokenUsageDetailText);
            flyoutContent.Children.Add(_resetTokenUsageStatsButton);
            SettingsDialogStyler.ApplyCompactStyleToLogicalTree(flyoutContent);

            return new DropDownButton
            {
                Content = _getString("SettingsLlmTokenUsageStatsButton", "Cached token 통계 보기"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Flyout = new Flyout
                {
                    Content = flyoutContent
                }
            };
        }

        private void RefreshTokenUsageStatsDisplay()
        {
            LlmTokenUsageStats stats = _llmService.TokenUsageStats;
            if (!stats.HasAny)
            {
                string empty = _getString("SettingsLlmTokenUsageStatsEmpty", "아직 관측된 LLM token usage가 없습니다.");
                _tokenUsageSummaryText.Text = empty;
                _tokenUsageDetailText.Text = empty;
                return;
            }

            string summary = string.Format(
                _getString("SettingsLlmTokenUsageStatsSummaryFormat", "누적: 요청 {0:N0}회 · 입력 {1:N0} · 출력 {2:N0} · 전체 {3:N0} · cached {4:N0} 토큰"),
                stats.RequestCount,
                stats.PromptTokens,
                stats.CompletionTokens,
                stats.TotalTokens,
                stats.CachedTokens);
            _tokenUsageSummaryText.Text = summary;

            var details = new StringBuilder();
            details.AppendLine(summary);
            if (stats.LastUsage != null)
            {
                details.AppendLine(string.Format(
                    _getString("SettingsLlmTokenUsageStatsLastFormat", "마지막: {0} / {1} · cached {2:N0} 토큰"),
                    string.IsNullOrWhiteSpace(stats.LastUsage.Provider) ? _getString("SettingsLlmTokenUsageUnknownProvider", "알 수 없는 공급자") : stats.LastUsage.Provider,
                    string.IsNullOrWhiteSpace(stats.LastUsage.Model) ? _getString("SettingsLlmTokenUsageUnknownModel", "알 수 없는 모델") : stats.LastUsage.Model,
                    stats.LastUsage.CachedTokens ?? 0));
            }

            details.AppendLine();
            details.AppendLine(_getString("SettingsLlmTokenUsageStatsByProviderModel", "공급자/모델별"));
            foreach (var bucket in stats.ByProviderModel)
            {
                details.AppendLine(string.Format(
                    _getString("SettingsLlmTokenUsageStatsBucketLineFormat", "{0} / {1}: 요청 {2:N0}회, cached {3:N0}, 전체 {4:N0} 토큰"),
                    bucket.Provider,
                    bucket.Model,
                    bucket.RequestCount,
                    bucket.CachedTokens,
                    bucket.TotalTokens));
            }

            details.AppendLine();
            details.AppendLine(_getString("SettingsLlmTokenUsageStatsByDay", "일별"));
            foreach (var bucket in stats.ByDay)
            {
                details.AppendLine(string.Format(
                    _getString("SettingsLlmTokenUsageStatsPeriodLineFormat", "{0}: 요청 {1:N0}회, cached {2:N0}, 전체 {3:N0} 토큰"),
                    bucket.Period,
                    bucket.RequestCount,
                    bucket.CachedTokens,
                    bucket.TotalTokens));
            }

            details.AppendLine();
            details.AppendLine(_getString("SettingsLlmTokenUsageStatsByMonth", "월별"));
            foreach (var bucket in stats.ByMonth)
            {
                details.AppendLine(string.Format(
                    _getString("SettingsLlmTokenUsageStatsPeriodLineFormat", "{0}: 요청 {1:N0}회, cached {2:N0}, 전체 {3:N0} 토큰"),
                    bucket.Period,
                    bucket.RequestCount,
                    bucket.CachedTokens,
                    bucket.TotalTokens));
            }

            _tokenUsageDetailText.Text = details.ToString().TrimEnd();
        }

        private string GetSelectedProviderName()
        {
            return _llmProviderCombo.SelectedItem as string ?? "OpenAI";
        }

        private void AddModelChoice(string model)
        {
            if (!string.IsNullOrWhiteSpace(model) && !_llmModelCombo.Items.Contains(model))
            {
                _llmModelCombo.Items.Add(model);
            }
        }

        private void SelectModelChoice(string model)
        {
            AddModelChoice(model);
            if (!string.IsNullOrWhiteSpace(model))
            {
                _llmModelCombo.SelectedItem = model;
            }
            else if (_llmModelCombo.Items.Count > 0)
            {
                _llmModelCombo.SelectedIndex = 0;
            }
        }

        private void PopulateModelChoices(string provider, string selectedModel)
        {
            _llmModelCombo.Items.Clear();
            foreach (string model in SettingsLlmModelCatalog.GetStaticModels(provider))
            {
                AddModelChoice(model);
            }

            SelectModelChoice(SettingsLlmModelCatalog.GetInitialModel(_settings, provider, selectedModel));
        }

        private void ApplyProviderDefaults(string provider)
        {
            if (!SettingsLlmModelCatalog.IsKnownDefaultEndpoint(_llmEndpointBox.Text.Trim()))
            {
                return;
            }

            _llmEndpointBox.Text = SettingsLlmModelCatalog.GetDefaultEndpoint(provider, _llmEndpointBox.Text);
        }

        private async Task RefreshModelsAsync()
        {
            string provider = GetSelectedProviderName();
            if (!SettingsLlmModelCatalog.SupportsRemoteModelFetch(provider))
            {
                _modelStatusText.Text = _getString("SettingsLlmLoadModelsNotSupported", "해당 공급자는 모델 불러오기를 지원하지 않습니다.");
                return;
            }

            try
            {
                _refreshModelsButton.IsEnabled = false;
                _modelStatusText.Text = string.Format(_getString("SettingsLlmLoadingModelsFormat", "{0} 모델 목록을 불러오는 중입니다..."), provider);
                string apiKey = _llmApiKeyBox.Password.Trim();
                var models = await SettingsLlmModelFetcher.FetchModelsAsync(_llmEndpointBox.Text.Trim(), apiKey);

                _llmModelCombo.Items.Clear();
                foreach (var model in models)
                {
                    AddModelChoice(model);
                }

                string targetModel = SettingsLlmModelCatalog.GetRemoteFetchSelection(_settings, provider);
                SelectModelChoice(models.Contains(targetModel) ? targetModel : models.FirstOrDefault() ?? targetModel);
                _modelStatusText.Text = models.Count > 0
                    ? string.Format(_getString("SettingsLlmModelsLoadedFormat", "{0}개 모델을 불러왔습니다."), models.Count)
                    : string.Format(_getString("SettingsLlmNoModelsFoundFormat", "{0}에서 사용 가능한 모델을 찾지 못했습니다."), provider);
            }
            catch (Exception ex)
            {
                SelectModelChoice(SettingsLlmModelCatalog.GetRemoteFetchSelection(_settings, provider));
                _modelStatusText.Text = string.Format(_getString("SettingsLlmLoadModelsFailedFormat", "{0} 모델 목록을 불러오지 못했습니다: {1}"), provider, ex.Message);
            }
            finally
            {
                _refreshModelsButton.IsEnabled = true;
            }
        }

        private void UpdateModelRefreshButtonVisibility()
        {
            string provider = GetSelectedProviderName();
            _llmThinkingLevelCombo.Visibility = SettingsLlmModelCatalog.SupportsThinkingLevel(provider) ? Visibility.Visible : Visibility.Collapsed;

            if (provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                _refreshModelsButton.Content = _getString("SettingsLlmLoadModels", "LM Studio 모델 불러오기");
                _refreshModelsButton.Visibility = Visibility.Visible;
                _modelStatusText.Text = _getString("SettingsLlmInfo", "LM Studio는 서버가 켜져 있을 때 http://localhost:1234/v1/models 에서 모델 목록을 불러옵니다.");
                _modelStatusText.Visibility = Visibility.Visible;
            }
            else if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                _refreshModelsButton.Content = _getString("SettingsLlmLoadOpenRouterModels", "OpenRouter 모델 불러오기");
                _refreshModelsButton.Visibility = Visibility.Visible;
                _modelStatusText.Text = _getString("SettingsLlmOpenRouterInfo", "OpenRouter는 https://openrouter.ai/api/v1/models 에서 모델 목록을 불러옵니다.");
                _modelStatusText.Visibility = Visibility.Visible;
            }
            else if (provider.Equals("Cerebras", StringComparison.OrdinalIgnoreCase))
            {
                _refreshModelsButton.Content = _getString("SettingsLlmLoadCerebrasModels", "Cerebras 모델 불러오기");
                _refreshModelsButton.Visibility = Visibility.Visible;
                _modelStatusText.Text = _getString("SettingsLlmCerebrasInfo", "Cerebras는 https://api.cerebras.ai/v1/models 에서 모델 목록을 불러옵니다.");
                _modelStatusText.Visibility = Visibility.Visible;
            }
            else if (provider.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase))
            {
                _refreshModelsButton.Content = _getString("SettingsLlmLoadOpenCodeGoModels", "OpenCode Go 모델 불러오기");
                _refreshModelsButton.Visibility = Visibility.Visible;
                _modelStatusText.Text = _getString("SettingsLlmOpenCodeGoInfo", "OpenCode Go는 https://opencode.ai/zen/go/v1/models 에서 모델 목록을 불러옵니다.");
                _modelStatusText.Visibility = Visibility.Visible;
            }
            else if (provider.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase))
            {
                _refreshModelsButton.Content = _getString("SettingsLlmLoadOpenCodeZenModels", "OpenCode Zen 모델 불러오기");
                _refreshModelsButton.Visibility = Visibility.Visible;
                _modelStatusText.Text = _getString("SettingsLlmOpenCodeZenInfo", "OpenCode Zen는 https://opencode.ai/zen/v1/models 에서 모델 목록을 불러옵니다.");
                _modelStatusText.Visibility = Visibility.Visible;
            }
            else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                _refreshModelsButton.Content = _getString("SettingsLlmLoadOllamaModels", "Ollama 모델 불러오기");
                _refreshModelsButton.Visibility = Visibility.Visible;
                _modelStatusText.Text = _getString("SettingsLlmOllamaInfo", "Ollama는 서버가 켜져 있을 때 http://localhost:11434/v1/models 에서 모델 목록을 불러옵니다.");
                _modelStatusText.Visibility = Visibility.Visible;
            }
            else if (provider.Equals("Ollama Cloud", StringComparison.OrdinalIgnoreCase))
            {
                _refreshModelsButton.Content = _getString("SettingsLlmLoadOllamaCloudModels", "Ollama Cloud 모델 불러오기");
                _refreshModelsButton.Visibility = Visibility.Visible;
                _modelStatusText.Text = _getString("SettingsLlmOllamaCloudInfo", "Ollama Cloud는 지정된 endpoint에서 모델 목록을 불러옵니다.");
                _modelStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                _refreshModelsButton.Visibility = Visibility.Collapsed;
                _modelStatusText.Visibility = Visibility.Collapsed;
            }
        }

        private ComboBox CreateProviderCombo(EditorSettings settings)
        {
            var comboBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var providerName in SettingsLlmModelCatalog.ProviderNames)
            {
                comboBox.Items.Add(providerName);
            }

            comboBox.SelectedIndex = SettingsLlmModelCatalog.GetProviderIndex(settings.LlmProvider);
            return comboBox;
        }

        private static ComboBox CreateSourceLanguageCombo(EditorSettings settings, Func<string, string, string> getString)
        {
            var comboBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            comboBox.Items.Add(getString("LlmLangAutoDetect", "자동 감지 (Auto Detect)"));
            comboBox.Items.Add(getString("LlmLangKorean", "한국어 (Korean)"));
            comboBox.Items.Add(getString("LlmLangEnglish", "영어 (English)"));
            comboBox.Items.Add(getString("LlmLangJapanese", "일본어 (Japanese)"));
            comboBox.Items.Add(getString("LlmLangChineseSimplified", "중국어 간체 (Simplified Chinese)"));
            comboBox.Items.Add(getString("LlmLangChineseTraditional", "중국어 번체 (Traditional Chinese)"));
            comboBox.Items.Add(getString("LlmLangFrench", "프랑스어 (French)"));
            comboBox.Items.Add(getString("LlmLangSpanish", "스페인어 (Spanish)"));
            comboBox.Items.Add(getString("LlmLangGerman", "독일어 (German)"));
            comboBox.SelectedIndex = settings.LlmSourceLanguage switch
            {
                "Korean" => 1,
                "English" => 2,
                "Japanese" => 3,
                "Chinese" => 4,
                "Chinese Simplified" => 4,
                "Chinese Traditional" => 5,
                "French" => 6,
                "Spanish" => 7,
                "German" => 8,
                _ => 0
            };
            return comboBox;
        }

        private static ComboBox CreateTargetLanguageCombo(EditorSettings settings, Func<string, string, string> getString)
        {
            var comboBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            comboBox.Items.Add(getString("LlmTargetLanguageDefault", "기본값 (UI 언어)"));
            comboBox.Items.Add(getString("LlmLangKorean", "한국어 (Korean)"));
            comboBox.Items.Add(getString("LlmLangEnglish", "영어 (English)"));
            comboBox.Items.Add(getString("LlmLangJapanese", "일본어 (Japanese)"));
            comboBox.Items.Add(getString("LlmLangChineseSimplified", "중국어 간체 (Simplified Chinese)"));
            comboBox.Items.Add(getString("LlmLangChineseTraditional", "중국어 번체 (Traditional Chinese)"));
            comboBox.Items.Add(getString("LlmLangFrench", "프랑스어 (French)"));
            comboBox.Items.Add(getString("LlmLangSpanish", "스페인어 (Spanish)"));
            comboBox.Items.Add(getString("LlmLangGerman", "독일어 (German)"));
            comboBox.SelectedIndex = settings.LlmTargetLanguage switch
            {
                "Korean" => 1,
                "English" => 2,
                "Japanese" => 3,
                "Chinese" => 4,
                "Chinese Simplified" => 4,
                "Chinese Traditional" => 5,
                "French" => 6,
                "Spanish" => 7,
                "German" => 8,
                _ => 0
            };
            return comboBox;
        }

        private static ComboBox CreateThinkingLevelCombo(EditorSettings settings, Func<string, string, string> getString)
        {
            var comboBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
            comboBox.Items.Add(getString("SettingsLlmThinkingLevelDefault", "기본값 (Default)"));
            comboBox.Items.Add(getString("SettingsLlmThinkingLevelDisabled", "비활성화 (Disable)"));
            comboBox.Items.Add(getString("SettingsLlmThinkingLevelLow", "낮음 (Low)"));
            comboBox.Items.Add(getString("SettingsLlmThinkingLevelMedium", "중간 (Medium)"));
            comboBox.Items.Add(getString("SettingsLlmThinkingLevelHigh", "높음 (High)"));
            comboBox.Items.Add(getString("SettingsLlmThinkingLevelXHigh", "최고 (X-High)"));
            comboBox.SelectedIndex = settings.LlmThinkingLevel.ToLowerInvariant() switch
            {
                "disabled" => 1,
                "low" => 2,
                "medium" => 3,
                "high" => 4,
                "xhigh" => 5,
                _ => 0
            };
            return comboBox;
        }
    }
}
