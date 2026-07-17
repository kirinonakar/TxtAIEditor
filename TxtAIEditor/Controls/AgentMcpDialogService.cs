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
        public string Transport { get; set; } = AgentMcpTransportTypes.Http;
        public string Endpoint { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = "[]";
        public string WorkingDirectory { get; set; } = string.Empty;
        public string EnvironmentJson { get; set; } = "{}";
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

    internal sealed class AgentMcpBrowserUseSettingsInput
    {
        public bool AllowInteraction { get; set; } = true;
        public bool CaptureEnabled { get; set; } = true;
        public bool ComputerUseEnabled { get; set; }
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

            var workflowExplorerButton = CreateExplorerButton(workflowDirectoryBox);

            var stack = new StackPanel { Spacing = 10, Width = 460 };
            stack.Children.Add(CreateLabel(_getString("AgentMcpComfyUiLaunchPathLabel", "ComfyUI 실행 파일")));
            stack.Children.Add(CreatePickerRow(launchPathBox, launchBrowseButton));
            stack.Children.Add(CreateLabel(_getString("AgentMcpComfyUiWorkflowDirectoryLabel", "워크플로우(API) 폴더")));
            stack.Children.Add(CreatePickerRow(workflowDirectoryBox, workflowBrowseButton, workflowExplorerButton));
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

        public async Task<AgentMcpBrowserUseSettingsInput?> ShowBrowserUseSettingsAsync(AgentMcpBrowserUseSettingsInput initial)
        {
            var interactionToggle = new ToggleSwitch
            {
                Header = _getString("AgentMcpBrowserUseAllowInteractionLabel", "클릭, 키 입력 및 스크롤 허용"),
                IsOn = initial.AllowInteraction
            };
            var captureToggle = new ToggleSwitch
            {
                Header = _getString("AgentMcpBrowserUseCaptureEnabledLabel", "이미지 캡처 사용"),
                IsOn = initial.CaptureEnabled
            };
            var computerUseToggle = new ToggleSwitch
            {
                Header = _getString("AgentMcpBrowserUseComputerUseEnabledLabel", "Computer Use - 다른 프로그램 조작"),
                IsOn = initial.ComputerUseEnabled
            };

            var stack = new StackPanel { Spacing = 12, Width = 460 };
            stack.Children.Add(captureToggle);
            stack.Children.Add(interactionToggle);
            stack.Children.Add(computerUseToggle);
            stack.Children.Add(CreateInfoText(_getString(
                "AgentMcpBrowserUseSettingsInfo",
                "Browser Use는 Windows 기본 브라우저를 실행하고 키보드·마우스 입력으로 조작합니다. 읽기 전용 URL 열기, 상태 확인 및 페이지 텍스트 읽기는 이 옵션과 관계없이 사용할 수 있습니다.")));
            stack.Children.Add(CreateInfoText(_getString(
                "AgentMcpBrowserUseComputerUseInfo",
                "Computer Use를 켜면 Agent가 실행 중인 다른 프로그램 창을 선택하거나 프로그램을 실행하고, 동일한 이미지 캡처·클릭·키 입력 도구로 조작할 수 있습니다.")));

            var dialog = new ContentDialog
            {
                Title = _getString("AgentMcpBrowserUseSettingsTitle", "Browser Use 설정"),
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

            return new AgentMcpBrowserUseSettingsInput
            {
                AllowInteraction = interactionToggle.IsOn,
                CaptureEnabled = captureToggle.IsOn,
                ComputerUseEnabled = computerUseToggle.IsOn
            };
        }

        private async Task<AgentMcpDialogInput?> ShowAsync(
            AgentMcpDialogInput initial,
            string title,
            string primaryButtonText)
        {
            var nameBox = CreateTextBox(_getString("AgentMcpNamePlaceholder", "MCP 이름 입력..."));
            nameBox.Text = initial.Name;
            var transportBox = CreateTransportComboBox(initial.Transport);
            var endpointLabel = CreateLabel(_getString("AgentMcpEndpointLabel", "MCP 주소"));
            var endpointBox = CreateTextBox(_getString("AgentMcpEndpointPlaceholder", "https://server.example/mcp"));
            endpointBox.Text = initial.Endpoint;
            var commandLabel = CreateLabel(_getString("AgentMcpCommandLabel", "실행 명령"));
            var commandBox = CreateTextBox(_getString("AgentMcpCommandPlaceholder", "npx"));
            commandBox.Text = initial.Command;
            var argumentsLabel = CreateLabel(_getString("AgentMcpArgumentsLabel", "인수 (JSON 배열)"));
            var argumentsBox = CreateJsonTextBox(_getString("AgentMcpArgumentsPlaceholder", "[\"-y\", \"@modelcontextprotocol/server-filesystem\", \"D:\\\\workspace\"]"));
            argumentsBox.Text = initial.ArgumentsJson;
            var workingDirectoryLabel = CreateLabel(_getString("AgentMcpWorkingDirectoryLabel", "작업 폴더 (선택)"));
            var workingDirectoryBox = CreateTextBox(_getString("AgentMcpWorkingDirectoryPlaceholder", "비워두면 현재 작업 영역 사용"));
            workingDirectoryBox.Text = initial.WorkingDirectory;
            var environmentLabel = CreateLabel(_getString("AgentMcpEnvironmentLabel", "환경 변수 (JSON 객체, 선택)"));
            var environmentBox = CreateJsonTextBox(_getString("AgentMcpEnvironmentPlaceholder", "{\"KEY\": \"value\"}"));
            environmentBox.Text = initial.EnvironmentJson;
            var stdioInfo = CreateInfoText(_getString(
                "AgentMcpStdioInfo",
                "명령은 로컬 프로세스로 실행됩니다. 예: npx와 JSON 인수 [\"-y\", \"@modelcontextprotocol/server-filesystem\", \"D:\\\\workspace\"]"));
            var authTypeBox = CreateAuthTypeComboBox(initial.AuthType);
            var authTypeLabel = CreateLabel(_getString("AgentMcpAuthTypeLabel", "인증 방식"));
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

            void UpdateFieldVisibility()
            {
                bool isStdio = GetSelectedTransport(transportBox).Equals(AgentMcpTransportTypes.Stdio, StringComparison.OrdinalIgnoreCase);
                SetVisible(endpointLabel, !isStdio);
                SetVisible(endpointBox, !isStdio);
                SetVisible(commandLabel, isStdio);
                SetVisible(commandBox, isStdio);
                SetVisible(argumentsLabel, isStdio);
                SetVisible(argumentsBox, isStdio);
                SetVisible(workingDirectoryLabel, isStdio);
                SetVisible(workingDirectoryBox, isStdio);
                SetVisible(environmentLabel, isStdio);
                SetVisible(environmentBox, isStdio);
                SetVisible(stdioInfo, isStdio);
                SetVisible(authTypeLabel, !isStdio);
                SetVisible(authTypeBox, !isStdio);
                UpdateAuthFieldVisibility(
                authTypeBox,
                !isStdio,
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
            }

            transportBox.SelectionChanged += (_, _) => UpdateFieldVisibility();
            authTypeBox.SelectionChanged += (_, _) => UpdateFieldVisibility();
            UpdateFieldVisibility();

            var stack = new StackPanel { Spacing = 10, Width = 420 };
            stack.Children.Add(CreateLabel(_getString("AgentMcpNameLabel", "MCP 이름")));
            stack.Children.Add(nameBox);
            stack.Children.Add(CreateLabel(_getString("AgentMcpTransportLabel", "연결 방식")));
            stack.Children.Add(transportBox);
            stack.Children.Add(endpointLabel);
            stack.Children.Add(endpointBox);
            stack.Children.Add(commandLabel);
            stack.Children.Add(commandBox);
            stack.Children.Add(argumentsLabel);
            stack.Children.Add(argumentsBox);
            stack.Children.Add(workingDirectoryLabel);
            stack.Children.Add(workingDirectoryBox);
            stack.Children.Add(environmentLabel);
            stack.Children.Add(environmentBox);
            stack.Children.Add(stdioInfo);
            stack.Children.Add(authTypeLabel);
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
            stack.Children.Add(CreateInfoText(_getString("AgentMcpCredentialInfo", "API Key, OAuth Client Secret, OAuth 토큰, stdio 환경 변수는 설정 파일에 저장하지 않고 Windows 자격 증명 관리자에 저장합니다.")));

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
                Transport = GetSelectedTransport(transportBox),
                Endpoint = endpointBox.Text?.Trim() ?? string.Empty,
                Command = commandBox.Text?.Trim() ?? string.Empty,
                ArgumentsJson = argumentsBox.Text?.Trim() ?? "[]",
                WorkingDirectory = workingDirectoryBox.Text?.Trim() ?? string.Empty,
                EnvironmentJson = environmentBox.Text?.Trim() ?? "{}",
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

        private ComboBox CreateTransportComboBox(string selectedTransport)
        {
            var comboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32
            };
            comboBox.Items.Add(CreateTransportItem(AgentMcpTransportTypes.Http, _getString("AgentMcpTransportHttp", "HTTP / SSE")));
            comboBox.Items.Add(CreateTransportItem(AgentMcpTransportTypes.Stdio, _getString("AgentMcpTransportStdio", "stdio (로컬 프로세스)")));

            selectedTransport = AgentMcpTransportTypes.Normalize(selectedTransport, null);
            comboBox.SelectedItem = comboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, selectedTransport, StringComparison.OrdinalIgnoreCase));
            comboBox.SelectedIndex = comboBox.SelectedIndex < 0 ? 0 : comboBox.SelectedIndex;
            return comboBox;
        }

        private static ComboBoxItem CreateTransportItem(string transport, string label)
        {
            return new ComboBoxItem { Tag = transport, Content = label };
        }

        private static string GetSelectedTransport(ComboBox comboBox)
        {
            return comboBox.SelectedItem is ComboBoxItem item && item.Tag is string transport
                ? transport
                : AgentMcpTransportTypes.Http;
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
            bool authEnabled,
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
            bool showApiKey = authEnabled && authType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase);
            bool showBearer = authEnabled && authType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase);
            bool showBrowserOAuth = authEnabled && authType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase);

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

        private static TextBox CreateJsonTextBox(string placeholder)
        {
            return new TextBox
            {
                PlaceholderText = placeholder,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 56,
                MaxHeight = 110,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                VerticalContentAlignment = VerticalAlignment.Top
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

        private Button CreateExplorerButton(TextBox pathBox)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = "\uE8DA", FontSize = 12 },
                Width = 34,
                Height = 32,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            ToolTipService.SetToolTip(button, _getString("AgentMcpComfyUiOpenFolderTooltip", "Windows 탐색기에서 열기"));
            button.Click += (_, _) =>
            {
                string path = pathBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path))
                {
                    return;
                }

                try
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        UseShellExecute = false
                    };
                    startInfo.ArgumentList.Add(path);
                    System.Diagnostics.Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to open directory: {ex.Message}");
                }
            };
            return button;
        }

        private static Grid CreatePickerRow(TextBox pathBox, Button browseButton, Button? actionButton = null)
        {
            var grid = new Grid { ColumnSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (actionButton != null)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            Grid.SetColumn(pathBox, 0);
            Grid.SetColumn(browseButton, 1);
            grid.Children.Add(pathBox);
            grid.Children.Add(browseButton);
            if (actionButton != null)
            {
                Grid.SetColumn(actionButton, 2);
                grid.Children.Add(actionButton);
            }
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
