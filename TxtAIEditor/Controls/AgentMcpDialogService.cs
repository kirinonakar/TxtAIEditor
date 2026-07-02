using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpDialogInput
    {
        public string Name { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string AuthType { get; set; } = AuthTypeNone;
        public string HeaderName { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string OAuthAccessToken { get; set; } = string.Empty;
        public string OAuthClientId { get; set; } = string.Empty;
        public string OAuthClientSecret { get; set; } = string.Empty;
        public string OAuthAuthorizationEndpoint { get; set; } = string.Empty;
        public string OAuthTokenEndpoint { get; set; } = string.Empty;
        public string OAuthScopes { get; set; } = string.Empty;
    }

    internal sealed class AgentMcpComfyUiSettingsInput
    {
        public string LaunchPath { get; set; } = string.Empty;
        public string WorkflowDirectory { get; set; } = string.Empty;
    }

    internal sealed class AgentMcpDialogService
    {
        private readonly AgentPane _agentPane;
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string, string, string> _getString;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;

        public AgentMcpDialogService(
            AgentPane agentPane,
            Action<object> initializePickerWindow,
            Func<string, string, string> getString,
            Action? beforeDialog,
            Action? afterDialog)
        {
            _agentPane = agentPane;
            _initializePickerWindow = initializePickerWindow;
            _getString = getString;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
        }

        public Task<AgentMcpDialogInput?> ShowAddAsync()
        {
            return ShowAsync(
                new AgentMcpDialogInput { AuthType = AuthTypeNone },
                _getString("AgentMcpAddText", "MCP 추가"),
                _getString("AgentMcpSaveAddButton", "추가"));
        }

        public Task<AgentMcpDialogInput?> ShowEditAsync(AgentMcpDialogInput initial)
        {
            return ShowAsync(
                initial,
                _getString("AgentMcpEditTitle", "MCP 수정"),
                _getString("AgentMcpEditSaveButton", "저장"));
        }

        public async Task<AgentMcpComfyUiSettingsInput?> ShowComfyUiSettingsAsync(AgentMcpComfyUiSettingsInput initial)
        {
            var launchPathBox = CreateTextBox(_getString("AgentMcpComfyUiLaunchPathPlaceholder", "run_nvidia_gpu.bat 경로"));
            launchPathBox.Text = initial.LaunchPath;
            var workflowDirectoryBox = CreateTextBox(_getString("AgentMcpComfyUiWorkflowDirectoryPlaceholder", "ComfyUI API workflow 폴더"));
            workflowDirectoryBox.Text = initial.WorkflowDirectory;

            var launchBrowseButton = CreateBrowseButton();
            launchBrowseButton.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.Desktop
                };
                _initializePickerWindow(picker);
                picker.FileTypeFilter.Add(".bat");
                picker.FileTypeFilter.Add(".cmd");
                picker.FileTypeFilter.Add(".exe");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    launchPathBox.Text = file.Path;
                }
            };

            var workflowBrowseButton = CreateBrowseButton();
            workflowBrowseButton.Click += async (_, _) =>
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                _initializePickerWindow(picker);
                picker.FileTypeFilter.Add("*");

                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    workflowDirectoryBox.Text = folder.Path;
                }
            };

            var stack = new StackPanel { Spacing = 10, Width = 460 };
            stack.Children.Add(CreateLabel(_getString("AgentMcpComfyUiLaunchPathLabel", "ComfyUI 실행 파일")));
            stack.Children.Add(CreatePickerRow(launchPathBox, launchBrowseButton));
            stack.Children.Add(CreateLabel(_getString("AgentMcpComfyUiWorkflowDirectoryLabel", "워크플로우(API) 폴더")));
            stack.Children.Add(CreatePickerRow(workflowDirectoryBox, workflowBrowseButton));
            stack.Children.Add(CreateInfoText(_getString(
                "AgentMcpComfyUiSettingsInfo",
                "ComfyUI 플러그인을 활성화하면 실행 파일 경로로 서버를 자동 실행하고, 지정한 API 워크플로우 폴더의 JSON 목록을 Agent에게 제공합니다.")));

            var dialog = new ContentDialog
            {
                Title = _getString("AgentMcpComfyUiSettingsTitle", "ComfyUI 설정"),
                Content = stack,
                PrimaryButtonText = _getString("SettingsSave", "저장"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            return new AgentMcpComfyUiSettingsInput
            {
                LaunchPath = launchPathBox.Text?.Trim() ?? string.Empty,
                WorkflowDirectory = workflowDirectoryBox.Text?.Trim() ?? string.Empty
            };
        }

        private async Task<AgentMcpDialogInput?> ShowAsync(
            AgentMcpDialogInput initial,
            string title,
            string primaryButtonText)
        {
            var nameBox = CreateTextBox(_getString("AgentMcpNamePlaceholder", "MCP 이름 입력..."));
            nameBox.Text = initial.Name;
            var endpointBox = CreateTextBox(_getString("AgentMcpEndpointPlaceholder", "https://server.example/mcp"));
            endpointBox.Text = initial.Endpoint;
            var authTypeBox = CreateAuthTypeComboBox(initial.AuthType);
            var headerNameLabel = CreateLabel(_getString("AgentMcpHeaderNameLabel", "API Key Header 이름"));
            var headerNameBox = CreateTextBox(_getString("AgentMcpHeaderNamePlaceholder", "Authorization"));
            headerNameBox.Text = initial.HeaderName;
            var apiKeyLabel = CreateLabel(_getString("AgentMcpApiKeyLabel", "API Key"));
            var apiKeyBox = CreatePasswordBox(_getString("AgentMcpApiKeyPlaceholder", "API Key 입력..."));
            apiKeyBox.Password = initial.ApiKey;
            var oauthTokenLabel = CreateLabel(_getString("AgentMcpOAuthTokenLabel", "OAuth Access Token"));
            var oauthTokenBox = CreatePasswordBox(_getString("AgentMcpOAuthTokenPlaceholder", "OAuth Access Token 입력..."));
            oauthTokenBox.Password = initial.OAuthAccessToken;
            var oauthClientIdLabel = CreateLabel(_getString("AgentMcpOAuthClientIdLabel", "OAuth Client ID"));
            var oauthClientIdBox = CreateTextBox(_getString("AgentMcpOAuthClientIdPlaceholder", "OAuth Client ID 입력..."));
            oauthClientIdBox.Text = initial.OAuthClientId;
            var oauthClientSecretLabel = CreateLabel(_getString("AgentMcpOAuthClientSecretLabel", "OAuth Client Secret"));
            var oauthClientSecretBox = CreatePasswordBox(_getString("AgentMcpOAuthClientSecretPlaceholder", "OAuth Client Secret 입력..."));
            oauthClientSecretBox.Password = initial.OAuthClientSecret;
            var oauthAuthorizationEndpointLabel = CreateLabel(_getString("AgentMcpOAuthAuthorizationEndpointLabel", "Authorization URL"));
            var oauthAuthorizationEndpointBox = CreateTextBox(_getString("AgentMcpOAuthAuthorizationEndpointPlaceholder", "https://auth.example.com/oauth/authorize"));
            oauthAuthorizationEndpointBox.Text = initial.OAuthAuthorizationEndpoint;
            var oauthTokenEndpointLabel = CreateLabel(_getString("AgentMcpOAuthTokenEndpointLabel", "Token URL"));
            var oauthTokenEndpointBox = CreateTextBox(_getString("AgentMcpOAuthTokenEndpointPlaceholder", "https://auth.example.com/oauth/token"));
            oauthTokenEndpointBox.Text = initial.OAuthTokenEndpoint;
            var oauthScopesLabel = CreateLabel(_getString("AgentMcpOAuthScopesLabel", "OAuth Scope"));
            var oauthScopesBox = CreateTextBox(_getString("AgentMcpOAuthScopesPlaceholder", "scope1 scope2"));
            oauthScopesBox.Text = initial.OAuthScopes;

            authTypeBox.SelectionChanged += (_, _) => UpdateAuthFieldVisibility(
                authTypeBox,
                headerNameLabel,
                headerNameBox,
                apiKeyLabel,
                apiKeyBox,
                oauthTokenLabel,
                oauthTokenBox,
                oauthClientIdLabel,
                oauthClientIdBox,
                oauthClientSecretLabel,
                oauthClientSecretBox,
                oauthAuthorizationEndpointLabel,
                oauthAuthorizationEndpointBox,
                oauthTokenEndpointLabel,
                oauthTokenEndpointBox,
                oauthScopesLabel,
                oauthScopesBox);
            UpdateAuthFieldVisibility(
                authTypeBox,
                headerNameLabel,
                headerNameBox,
                apiKeyLabel,
                apiKeyBox,
                oauthTokenLabel,
                oauthTokenBox,
                oauthClientIdLabel,
                oauthClientIdBox,
                oauthClientSecretLabel,
                oauthClientSecretBox,
                oauthAuthorizationEndpointLabel,
                oauthAuthorizationEndpointBox,
                oauthTokenEndpointLabel,
                oauthTokenEndpointBox,
                oauthScopesLabel,
                oauthScopesBox);

            var stack = new StackPanel { Spacing = 10, Width = 420 };
            stack.Children.Add(CreateLabel(_getString("AgentMcpNameLabel", "MCP 이름")));
            stack.Children.Add(nameBox);
            stack.Children.Add(CreateLabel(_getString("AgentMcpEndpointLabel", "MCP 주소")));
            stack.Children.Add(endpointBox);
            stack.Children.Add(CreateLabel(_getString("AgentMcpAuthTypeLabel", "인증 방식")));
            stack.Children.Add(authTypeBox);
            stack.Children.Add(headerNameLabel);
            stack.Children.Add(headerNameBox);
            stack.Children.Add(apiKeyLabel);
            stack.Children.Add(apiKeyBox);
            stack.Children.Add(oauthTokenLabel);
            stack.Children.Add(oauthTokenBox);
            stack.Children.Add(oauthClientIdLabel);
            stack.Children.Add(oauthClientIdBox);
            stack.Children.Add(oauthClientSecretLabel);
            stack.Children.Add(oauthClientSecretBox);
            stack.Children.Add(oauthAuthorizationEndpointLabel);
            stack.Children.Add(oauthAuthorizationEndpointBox);
            stack.Children.Add(oauthTokenEndpointLabel);
            stack.Children.Add(oauthTokenEndpointBox);
            stack.Children.Add(oauthScopesLabel);
            stack.Children.Add(oauthScopesBox);
            stack.Children.Add(CreateInfoText(_getString("AgentMcpCredentialInfo", "API Key, OAuth Client Secret, OAuth 토큰은 설정 파일에 저장하지 않고 Windows 자격 증명 관리자에 저장합니다.")));

            var dialog = new ContentDialog
            {
                Title = title,
                Content = stack,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            return new AgentMcpDialogInput
            {
                Name = nameBox.Text?.Trim() ?? string.Empty,
                Endpoint = endpointBox.Text?.Trim() ?? string.Empty,
                AuthType = GetSelectedAuthType(authTypeBox),
                HeaderName = headerNameBox.Text,
                ApiKey = apiKeyBox.Password,
                OAuthAccessToken = oauthTokenBox.Password,
                OAuthClientId = oauthClientIdBox.Text,
                OAuthClientSecret = oauthClientSecretBox.Password,
                OAuthAuthorizationEndpoint = oauthAuthorizationEndpointBox.Text,
                OAuthTokenEndpoint = oauthTokenEndpointBox.Text,
                OAuthScopes = oauthScopesBox.Text
            };
        }

        private ComboBox CreateAuthTypeComboBox(string selectedAuthType)
        {
            var comboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32
            };
            comboBox.Items.Add(CreateAuthTypeItem(AuthTypeNone, _getString("AgentMcpAuthTypeNone", "없음")));
            comboBox.Items.Add(CreateAuthTypeItem(AuthTypeApiKey, _getString("AgentMcpAuthTypeApiKey", "API Key Header")));
            comboBox.Items.Add(CreateAuthTypeItem(AuthTypeOAuthBearer, _getString("AgentMcpAuthTypeOAuthBearer", "OAuth Access Token")));
            comboBox.Items.Add(CreateAuthTypeItem(AuthTypeOAuthAuthorizationCode, _getString("AgentMcpAuthTypeOAuthBrowser", "OAuth 브라우저 로그인")));

            selectedAuthType = NormalizeAuthType(selectedAuthType, null);
            foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, selectedAuthType, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    break;
                }
            }

            comboBox.SelectedIndex = comboBox.SelectedIndex < 0 ? 0 : comboBox.SelectedIndex;
            return comboBox;
        }

        private static ComboBoxItem CreateAuthTypeItem(string authType, string label)
        {
            return new ComboBoxItem
            {
                Tag = authType,
                Content = label
            };
        }

        private static string GetSelectedAuthType(ComboBox comboBox)
        {
            return comboBox.SelectedItem is ComboBoxItem item && item.Tag is string authType
                ? authType
                : AuthTypeNone;
        }

        private static void UpdateAuthFieldVisibility(
            ComboBox authTypeBox,
            TextBlock headerNameLabel,
            TextBox headerNameBox,
            TextBlock apiKeyLabel,
            PasswordBox apiKeyBox,
            TextBlock oauthTokenLabel,
            PasswordBox oauthTokenBox,
            TextBlock oauthClientIdLabel,
            TextBox oauthClientIdBox,
            TextBlock oauthClientSecretLabel,
            PasswordBox oauthClientSecretBox,
            TextBlock oauthAuthorizationEndpointLabel,
            TextBox oauthAuthorizationEndpointBox,
            TextBlock oauthTokenEndpointLabel,
            TextBox oauthTokenEndpointBox,
            TextBlock oauthScopesLabel,
            TextBox oauthScopesBox)
        {
            string authType = GetSelectedAuthType(authTypeBox);
            bool showApiKey = authType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase);
            bool showBearer = authType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase);
            bool showBrowserOAuth = authType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase);

            SetVisible(headerNameLabel, showApiKey);
            SetVisible(headerNameBox, showApiKey);
            SetVisible(apiKeyLabel, showApiKey);
            SetVisible(apiKeyBox, showApiKey);
            SetVisible(oauthTokenLabel, showBearer);
            SetVisible(oauthTokenBox, showBearer);
            SetVisible(oauthClientIdLabel, showBrowserOAuth);
            SetVisible(oauthClientIdBox, showBrowserOAuth);
            SetVisible(oauthClientSecretLabel, showBrowserOAuth);
            SetVisible(oauthClientSecretBox, showBrowserOAuth);
            SetVisible(oauthAuthorizationEndpointLabel, showBrowserOAuth);
            SetVisible(oauthAuthorizationEndpointBox, showBrowserOAuth);
            SetVisible(oauthTokenEndpointLabel, showBrowserOAuth);
            SetVisible(oauthTokenEndpointBox, showBrowserOAuth);
            SetVisible(oauthScopesLabel, showBrowserOAuth);
            SetVisible(oauthScopesBox, showBrowserOAuth);
        }

        private static void SetVisible(UIElement element, bool isVisible)
        {
            element.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static TextBlock CreateLabel(string text)
        {
            return new TextBlock
            {
                Text = text
            };
        }

        private static TextBox CreateTextBox(string placeholder)
        {
            return new TextBox
            {
                PlaceholderText = placeholder,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private Button CreateBrowseButton()
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = "\uE8B7", FontSize = 12 },
                Width = 34,
                Height = 32,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            ToolTipService.SetToolTip(button, _getString("AgentMcpComfyUiBrowseButton", "찾아보기"));
            return button;
        }

        private static Grid CreatePickerRow(TextBox pathBox, Button browseButton)
        {
            var grid = new Grid { ColumnSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(pathBox, 0);
            Grid.SetColumn(browseButton, 1);
            grid.Children.Add(pathBox);
            grid.Children.Add(browseButton);
            return grid;
        }

        private static PasswordBox CreatePasswordBox(string placeholder)
        {
            return new PasswordBox
            {
                PlaceholderText = placeholder,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private TextBlock CreateInfoText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = CreateSecondaryTextBrush()
            };
        }

        private Brush CreateSecondaryTextBrush()
        {
            bool isLightTheme = _agentPane.ActualTheme == ElementTheme.Light ||
                (_agentPane.ActualTheme == ElementTheme.Default &&
                    Application.Current.RequestedTheme == ApplicationTheme.Light);

            return new SolidColorBrush(isLightTheme
                ? Windows.UI.Color.FromArgb(255, 75, 85, 99)
                : Windows.UI.Color.FromArgb(255, 229, 231, 235));
        }

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            _beforeDialog?.Invoke();
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                _afterDialog?.Invoke();
            }
        }
    }
}
