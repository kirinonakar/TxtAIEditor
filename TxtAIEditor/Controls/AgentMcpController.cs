using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Interfaces;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpServer
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string AuthType { get; set; } = string.Empty;
        public string OAuthAccessToken { get; set; } = string.Empty;
        public string OAuthRefreshToken { get; set; } = string.Empty;
        public string OAuthClientId { get; set; } = string.Empty;
        public string OAuthClientSecret { get; set; } = string.Empty;
        public string OAuthAuthorizationEndpoint { get; set; } = string.Empty;
        public string OAuthTokenEndpoint { get; set; } = string.Empty;
        public string OAuthScopes { get; set; } = string.Empty;
        public DateTimeOffset OAuthAccessTokenExpiresAt { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class AgentMcpItem
    {
        public string Name { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
        public bool IsBuiltIn { get; set; }
        public bool CanEdit { get; set; } = true;
        public bool CanDelete { get; set; } = true;
    }

    internal sealed class AgentMcpToolAlias
    {
        public string Alias { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string InputSchemaJson { get; set; } = "{}";
        public bool IsBuiltIn { get; set; }
    }

    internal sealed class AgentMcpController
    {
        private const string ProtocolVersion = "2025-06-18";
        private const string AuthTypeNone = "none";
        private const string AuthTypeApiKey = "apiKey";
        private const string AuthTypeOAuthBearer = "oauthBearer";
        private const string AuthTypeOAuthAuthorizationCode = "oauthAuthorizationCode";
        private const string BuiltInComfyUiId = "builtin-comfyui";
        private const string BuiltInComfyUiName = "ComfyUI";
        private const string BuiltInComfyUiAlias = "mcp_comfyui_generate_image";
        private const string BuiltInComfyUiToolName = "generate_image";
        private const string DefaultComfyUiEndpoint = "http://127.0.0.1:8188";
        private const int DefaultComfyUiTimeoutSeconds = 300;
        private const int DefaultComfyUiPollIntervalMs = 1000;
        private const string BuiltInComfyUiInputSchemaJson = """
        {
          "type": "object",
          "properties": {
            "apiJson": {
              "type": "string",
              "description": "ComfyUI workflow API JSON. Pass either a raw workflow object or a /prompt payload containing prompt."
            },
            "parameters": {
              "type": "object",
              "description": "Optional replacements. Keys replace {{key}} placeholders and dot paths such as 6.inputs.text."
            },
            "prompt": {
              "type": "string",
              "description": "Positive image prompt. If no explicit parameter path is provided, TxtAIEditor analyzes the workflow and fills a positive prompt text slot such as an empty StringConcatenate inputs.string_a linked from CLIPTextEncode."
            },
            "inputImagePath": {
              "type": "string",
              "description": "Optional local image path to upload to the ComfyUI input folder before running the workflow."
            },
            "inputImages": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional local image paths to upload to the ComfyUI input folder. Empty LoadImage inputs are filled in workflow order."
            },
            "outputFileName": {
              "type": "string",
              "description": "File name or path for the generated image. Relative paths are saved under the current workspace."
            },
            "endpoint": {
              "type": "string",
              "description": "ComfyUI base URL. Defaults to http://127.0.0.1:8188."
            },
            "timeoutSeconds": {
              "type": "integer",
              "default": 300
            },
            "outputNodeId": {
              "type": "string",
              "description": "Optional ComfyUI output node id to prefer when several image outputs exist."
            }
          },
          "required": ["apiJson", "outputFileName"]
        }
        """;
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly AgentPane _agentPane;
        private readonly Action<object> _initializePickerWindow;
        private readonly ICredentialService _credentialService;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Action _contextChanged;
        private readonly Func<string> _workspaceRootProvider;
        private readonly Func<string, Task>? _fileModifiedAsync;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly string _mcpFilePath;
        private readonly List<AgentMcpServer> _servers = new();
        private readonly HashSet<string> _selectedServerIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AgentMcpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AgentMcpToolAlias> _toolAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _serverStatus = new(StringComparer.OrdinalIgnoreCase);

        public AgentMcpController(
            AgentPane agentPane,
            Action<object> initializePickerWindow,
            ICredentialService credentialService,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Action contextChanged,
            Func<string> workspaceRootProvider,
            Func<string, Task>? fileModifiedAsync,
            Action? beforeDialog,
            Action? afterDialog)
        {
            _agentPane = agentPane;
            _initializePickerWindow = initializePickerWindow;
            _credentialService = credentialService;
            _showError = showError;
            _getString = getString;
            _contextChanged = contextChanged;
            _workspaceRootProvider = workspaceRootProvider;
            _fileModifiedAsync = fileModifiedAsync;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _mcpFilePath = Path.Combine(settingsDir, "agent-mcp-servers.json");
        }

        public async Task LoadAsync()
        {
            bool migratedPlaintextHeaders = false;
            bool migratedPlaintextOAuth = false;
            try
            {
                if (File.Exists(_mcpFilePath))
                {
                    string json = await File.ReadAllTextAsync(_mcpFilePath);
                    var loaded = JsonSerializer.Deserialize<List<AgentMcpServer>>(json);
                    if (loaded != null)
                    {
                        _servers.Clear();
                        _servers.AddRange(loaded
                            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Endpoint))
                            .Select(s =>
                            {
                                if (string.IsNullOrWhiteSpace(s.Id))
                                {
                                    s.Id = Guid.NewGuid().ToString("N");
                                }

                                s.Name = s.Name.Trim();
                                s.Endpoint = s.Endpoint.Trim();
                                s.Headers = NormalizeHeaders(s.Headers);
                                s.AuthType = NormalizeAuthType(s.AuthType, s.Headers);
                                migratedPlaintextHeaders |= s.Headers.Values.Any(value => !string.IsNullOrWhiteSpace(value));
                                migratedPlaintextOAuth |= !string.IsNullOrWhiteSpace(s.OAuthAccessToken) ||
                                    !string.IsNullOrWhiteSpace(s.OAuthRefreshToken) ||
                                    !string.IsNullOrWhiteSpace(s.OAuthClientSecret);
                                MoveInlineHeaderValuesToCredential(s);
                                MoveInlineOAuthSecretsToCredential(s);
                                return s;
                            }));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load MCP servers: {ex.Message}");
            }

            _selectedServerIds.RemoveWhere(id =>
                !IsBuiltInMcpId(id) &&
                _servers.All(server => !server.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
            if (migratedPlaintextHeaders || migratedPlaintextOAuth)
            {
                await SaveAsync();
                return;
            }

            RebuildAliases();
            UpdateUI();
        }

        public async Task AddMcpAsync()
        {
            var nameBox = CreateTextBox(_getString("AgentMcpNamePlaceholder", "MCP 이름 입력..."));
            var endpointBox = CreateTextBox(_getString("AgentMcpEndpointPlaceholder", "https://server.example/mcp"));
            var authTypeBox = CreateAuthTypeComboBox(AuthTypeNone);
            var headerNameLabel = CreateLabel(_getString("AgentMcpHeaderNameLabel", "API Key Header 이름"));
            var headerNameBox = CreateTextBox(_getString("AgentMcpHeaderNamePlaceholder", "Authorization"));
            var apiKeyLabel = CreateLabel(_getString("AgentMcpApiKeyLabel", "API Key"));
            var apiKeyBox = CreatePasswordBox(_getString("AgentMcpApiKeyPlaceholder", "API Key 입력..."));
            var oauthTokenLabel = CreateLabel(_getString("AgentMcpOAuthTokenLabel", "OAuth Access Token"));
            var oauthTokenBox = CreatePasswordBox(_getString("AgentMcpOAuthTokenPlaceholder", "OAuth Access Token 입력..."));
            var oauthClientIdLabel = CreateLabel(_getString("AgentMcpOAuthClientIdLabel", "OAuth Client ID"));
            var oauthClientIdBox = CreateTextBox(_getString("AgentMcpOAuthClientIdPlaceholder", "OAuth Client ID 입력..."));
            var oauthClientSecretLabel = CreateLabel(_getString("AgentMcpOAuthClientSecretLabel", "OAuth Client Secret"));
            var oauthClientSecretBox = CreatePasswordBox(_getString("AgentMcpOAuthClientSecretPlaceholder", "OAuth Client Secret 입력..."));
            var oauthAuthorizationEndpointLabel = CreateLabel(_getString("AgentMcpOAuthAuthorizationEndpointLabel", "Authorization URL"));
            var oauthAuthorizationEndpointBox = CreateTextBox(_getString("AgentMcpOAuthAuthorizationEndpointPlaceholder", "https://auth.example.com/oauth/authorize"));
            var oauthTokenEndpointLabel = CreateLabel(_getString("AgentMcpOAuthTokenEndpointLabel", "Token URL"));
            var oauthTokenEndpointBox = CreateTextBox(_getString("AgentMcpOAuthTokenEndpointPlaceholder", "https://auth.example.com/oauth/token"));
            var oauthScopesLabel = CreateLabel(_getString("AgentMcpOAuthScopesLabel", "OAuth Scope"));
            var oauthScopesBox = CreateTextBox(_getString("AgentMcpOAuthScopesPlaceholder", "scope1 scope2"));
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
                Title = _getString("AgentMcpAddText", "MCP 추가"),
                Content = stack,
                PrimaryButtonText = _getString("AgentMcpSaveAddButton", "추가"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string name = nameBox.Text?.Trim() ?? string.Empty;
            string endpoint = endpointBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    _getString("AgentMcpNameEndpointRequired", "MCP 이름과 주소를 입력해주세요."));
                return;
            }

            if (!IsValidHttpEndpoint(endpoint))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    _getString("AgentMcpEndpointInvalid", "MCP 주소는 http 또는 https URL이어야 합니다."));
                return;
            }

            string authType = GetSelectedAuthType(authTypeBox);
            if (!TryBuildAuthSettings(
                authType,
                headerNameBox.Text,
                apiKeyBox.Password,
                oauthTokenBox.Password,
                oauthClientIdBox.Text,
                oauthClientSecretBox.Password,
                oauthAuthorizationEndpointBox.Text,
                oauthTokenEndpointBox.Text,
                oauthScopesBox.Text,
                out var authSettings,
                out string authError))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    authError);
                return;
            }

            AgentMcpServer? savedServer;
            var existing = _servers.FirstOrDefault(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                DeleteCredentialsForRemovedHeaders(existing, authSettings.Headers);
                if (!IsOAuthAuthType(existing.AuthType) || !IsOAuthAuthType(authType))
                {
                    DeleteOAuthCredentials(existing);
                }

                existing.Endpoint = endpoint;
                ApplyAuthSettings(existing, authSettings, deleteEmptySecrets: true);
                _sessions.Remove(existing.Id);
                _serverStatus.Remove(existing.Id);
                savedServer = existing;
            }
            else
            {
                var server = new AgentMcpServer
                {
                    Name = name,
                    Endpoint = endpoint,
                };
                ApplyAuthSettings(server, authSettings, deleteEmptySecrets: true);
                _servers.Add(server);
                savedServer = server;
            }

            await SaveAsync();
            await RunInitialOAuthLoginIfNeededAsync(savedServer, _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"));
        }

        public async Task ExportMcpAsync()
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "agent-mcp-servers.json"
            };
            _initializePickerWindow(picker);
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string json = JsonSerializer.Serialize(_servers, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(file.Path, json);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentMcpExportErrorTitle", "MCP 내보내기 오류"),
                    string.Format(_getString("AgentMcpExportErrorMessage", "MCP 목록을 내보내는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        public async Task EditMcpAsync(string serverName)
        {
            if (IsBuiltInComfyUiName(serverName))
            {
                return;
            }

            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            var nameBox = CreateTextBox(_getString("AgentMcpNamePlaceholder", "MCP 이름 입력..."));
            nameBox.Text = server.Name;
            var endpointBox = CreateTextBox(_getString("AgentMcpEndpointPlaceholder", "https://server.example/mcp"));
            endpointBox.Text = server.Endpoint;
            server.AuthType = NormalizeAuthType(server.AuthType, server.Headers);
            var authTypeBox = CreateAuthTypeComboBox(server.AuthType);
            var existingHeader = server.Headers.FirstOrDefault();
            var headerNameLabel = CreateLabel(_getString("AgentMcpHeaderNameLabel", "API Key Header 이름"));
            var headerNameBox = CreateTextBox(_getString("AgentMcpHeaderNamePlaceholder", "Authorization"));
            headerNameBox.Text = existingHeader.Key ?? string.Empty;
            var apiKeyLabel = CreateLabel(_getString("AgentMcpApiKeyLabel", "API Key"));
            var apiKeyBox = CreatePasswordBox(_getString("AgentMcpApiKeyPlaceholder", "API Key 입력..."));
            apiKeyBox.Password = string.IsNullOrWhiteSpace(existingHeader.Key)
                ? string.Empty
                : GetHeaderSecret(server, existingHeader.Key, existingHeader.Value);
            var oauthTokenLabel = CreateLabel(_getString("AgentMcpOAuthTokenLabel", "OAuth Access Token"));
            var oauthTokenBox = CreatePasswordBox(_getString("AgentMcpOAuthTokenPlaceholder", "OAuth Access Token 입력..."));
            oauthTokenBox.Password = GetOAuthSecret(server);
            var oauthClientIdLabel = CreateLabel(_getString("AgentMcpOAuthClientIdLabel", "OAuth Client ID"));
            var oauthClientIdBox = CreateTextBox(_getString("AgentMcpOAuthClientIdPlaceholder", "OAuth Client ID 입력..."));
            oauthClientIdBox.Text = server.OAuthClientId;
            var oauthClientSecretLabel = CreateLabel(_getString("AgentMcpOAuthClientSecretLabel", "OAuth Client Secret"));
            var oauthClientSecretBox = CreatePasswordBox(_getString("AgentMcpOAuthClientSecretPlaceholder", "OAuth Client Secret 입력..."));
            oauthClientSecretBox.Password = GetOAuthClientSecret(server);
            var oauthAuthorizationEndpointLabel = CreateLabel(_getString("AgentMcpOAuthAuthorizationEndpointLabel", "Authorization URL"));
            var oauthAuthorizationEndpointBox = CreateTextBox(_getString("AgentMcpOAuthAuthorizationEndpointPlaceholder", "https://auth.example.com/oauth/authorize"));
            oauthAuthorizationEndpointBox.Text = server.OAuthAuthorizationEndpoint;
            var oauthTokenEndpointLabel = CreateLabel(_getString("AgentMcpOAuthTokenEndpointLabel", "Token URL"));
            var oauthTokenEndpointBox = CreateTextBox(_getString("AgentMcpOAuthTokenEndpointPlaceholder", "https://auth.example.com/oauth/token"));
            oauthTokenEndpointBox.Text = server.OAuthTokenEndpoint;
            var oauthScopesLabel = CreateLabel(_getString("AgentMcpOAuthScopesLabel", "OAuth Scope"));
            var oauthScopesBox = CreateTextBox(_getString("AgentMcpOAuthScopesPlaceholder", "scope1 scope2"));
            oauthScopesBox.Text = server.OAuthScopes;
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
                Title = _getString("AgentMcpEditTitle", "MCP 수정"),
                Content = stack,
                PrimaryButtonText = _getString("AgentMcpEditSaveButton", "저장"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string name = nameBox.Text?.Trim() ?? string.Empty;
            string endpoint = endpointBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    _getString("AgentMcpNameEndpointRequired", "MCP 이름과 주소를 입력해주세요."));
                return;
            }

            if (!IsValidHttpEndpoint(endpoint))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    _getString("AgentMcpEndpointInvalid", "MCP 주소는 http 또는 https URL이어야 합니다."));
                return;
            }

            string authType = GetSelectedAuthType(authTypeBox);
            if (!TryBuildAuthSettings(
                authType,
                headerNameBox.Text,
                apiKeyBox.Password,
                oauthTokenBox.Password,
                oauthClientIdBox.Text,
                oauthClientSecretBox.Password,
                oauthAuthorizationEndpointBox.Text,
                oauthTokenEndpointBox.Text,
                oauthScopesBox.Text,
                out var authSettings,
                out string authError))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    authError);
                return;
            }

            var duplicate = _servers.FirstOrDefault(item =>
                !item.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                DeleteAllCredentials(duplicate);
                _servers.Remove(duplicate);
                _selectedServerIds.Remove(duplicate.Id);
                _sessions.Remove(duplicate.Id);
                _serverStatus.Remove(duplicate.Id);
            }

            server.Name = name;
            server.Endpoint = endpoint;
            DeleteCredentialsForRemovedHeaders(server, authSettings.Headers);
            if (!IsOAuthAuthType(server.AuthType) || !IsOAuthAuthType(authType))
            {
                DeleteOAuthCredentials(server);
            }

            ApplyAuthSettings(server, authSettings, deleteEmptySecrets: true);
            _sessions.Remove(server.Id);
            _serverStatus.Remove(server.Id);
            await SaveAsync();
            await RunInitialOAuthLoginIfNeededAsync(server, _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"));
        }

        public async Task ImportMcpAsync()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            _initializePickerWindow(picker);
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(file.Path);
                var imported = DeserializeImportedServers(json);
                if (imported.Count == 0)
                {
                    throw new InvalidDataException(_getString("AgentMcpImportInvalidFile", "가져올 수 있는 MCP JSON이 아닙니다."));
                }

                foreach (var item in imported)
                {
                    string name = item.Name?.Trim() ?? string.Empty;
                    string endpoint = item.Endpoint?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.IsNullOrWhiteSpace(endpoint) ||
                        !IsValidHttpEndpoint(endpoint))
                    {
                        continue;
                    }

                    var existing = FindServer(name);
                    if (existing != null)
                    {
                        var headers = NormalizeHeaders(item.Headers);
                        DeleteCredentialsForRemovedHeaders(existing, headers);
                        existing.Endpoint = endpoint;
                        existing.Headers = StoreHeaderSecrets(existing, headers);
                        _sessions.Remove(existing.Id);
                        _serverStatus.Remove(existing.Id);
                    }
                    else
                    {
                        var server = new AgentMcpServer
                        {
                            Name = name,
                            Endpoint = endpoint
                        };
                        server.Headers = StoreHeaderSecrets(server, NormalizeHeaders(item.Headers));
                        _servers.Add(server);
                    }
                }

                await SaveAsync();
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentMcpImportErrorTitle", "MCP 가져오기 오류"),
                    string.Format(_getString("AgentMcpImportErrorMessage", "MCP 목록을 가져오는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        public async Task ToggleMcpAsync(string serverName)
        {
            if (IsBuiltInComfyUiName(serverName))
            {
                if (!_selectedServerIds.Add(BuiltInComfyUiId))
                {
                    _selectedServerIds.Remove(BuiltInComfyUiId);
                }

                RebuildAliases();
                UpdateUI();
                return;
            }

            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            if (!_selectedServerIds.Add(server.Id))
            {
                _selectedServerIds.Remove(server.Id);
                _sessions.Remove(server.Id);
                _serverStatus.Remove(server.Id);
                RebuildAliases();
                UpdateUI();
                return;
            }

            UpdateUI();
            await RefreshServerToolsAsync(server, CancellationToken.None);
        }

        public async Task DeleteMcpAsync(string serverName)
        {
            if (IsBuiltInComfyUiName(serverName))
            {
                return;
            }

            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            DeleteAllCredentials(server);
            _servers.Remove(server);
            _selectedServerIds.Remove(server.Id);
            _sessions.Remove(server.Id);
            _serverStatus.Remove(server.Id);
            await SaveAsync();
        }

        public void RemoveSelectedMcp(string serverName)
        {
            if (IsBuiltInComfyUiName(serverName))
            {
                _selectedServerIds.Remove(BuiltInComfyUiId);
                RebuildAliases();
                UpdateUI();
                return;
            }

            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            _selectedServerIds.Remove(server.Id);
            _sessions.Remove(server.Id);
            _serverStatus.Remove(server.Id);
            RebuildAliases();
            UpdateUI();
        }

        public string GetSelectedMcpLabel()
        {
            var labels = new List<string>();
            if (IsBuiltInComfyUiSelected())
            {
                labels.Add(BuiltInComfyUiName);
            }

            labels.AddRange(GetSelectedServers().Select(server => server.Name));
            return string.Join(", ", labels);
        }

        public async Task EnsureActiveToolsAsync(CancellationToken cancellationToken)
        {
            RebuildAliases();
            foreach (var server in GetSelectedServers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_sessions.TryGetValue(server.Id, out var session) || session.Tools.Count == 0)
                {
                    await RefreshServerToolsAsync(server, cancellationToken);
                }
            }
        }

        public string BuildSelectedMcpSection()
        {
            var selectedServers = GetSelectedServers();
            bool hasComfyUi = IsBuiltInComfyUiSelected();
            if (selectedServers.Count == 0 && !hasComfyUi)
            {
                return string.Empty;
            }

            RebuildAliases();
            var builder = new StringBuilder();
            builder.AppendLine("[Enabled MCP servers]");
            builder.AppendLine("These tools are provided by enabled MCP servers or built-in MCP-compatible plugins. Use their exact alias names in tool_call.");
            builder.AppendLine("MCP tool aliases use the form mcp_server_tool and are executed through MCP tools/call or the matching built-in plugin.");
            builder.AppendLine();

            if (hasComfyUi)
            {
                AppendMcpAliasSection(builder, BuiltInComfyUiName, BuiltInComfyUiId);
            }

            foreach (var server in selectedServers)
            {
                AppendMcpAliasSection(builder, server.Name, server.Id);
            }

            return builder.ToString().Trim();
        }

        private void AppendMcpAliasSection(StringBuilder builder, string serverName, string serverId)
        {
            builder.AppendLine($"## {serverName}");
            var aliases = _toolAliases.Values
                .Where(alias => alias.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(alias => alias.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (aliases.Count == 0)
            {
                builder.AppendLine("Status: tools not available");
                builder.AppendLine();
                return;
            }

            foreach (var alias in aliases)
            {
                builder.AppendLine($"- {alias.Alias}: {alias.Description}");
                builder.AppendLine($"  Original MCP tool: {alias.ToolName}");
                builder.AppendLine($"  Arguments JSON schema: {alias.InputSchemaJson}");
            }

            builder.AppendLine();
        }

        public bool TryGetToolAlias(string aliasName, out AgentMcpToolAlias alias)
        {
            return _toolAliases.TryGetValue(aliasName, out alias!);
        }

        public async Task<string> ExecuteToolAsync(string aliasName, JsonElement arguments, CancellationToken cancellationToken)
        {
            if (!TryGetToolAlias(aliasName, out var alias))
            {
                return $"MCP tool failed: unknown MCP tool alias: {aliasName}";
            }

            if (alias.IsBuiltIn)
            {
                return await ExecuteBuiltInToolAsync(alias, arguments, cancellationToken);
            }

            var server = _servers.FirstOrDefault(item => item.Id.Equals(alias.ServerId, StringComparison.OrdinalIgnoreCase));
            if (server == null)
            {
                return $"MCP tool failed: server not found for {aliasName}.";
            }

            AgentMcpSession session = await EnsureSessionAsync(server, cancellationToken);
            JsonElement callArguments = arguments.ValueKind == JsonValueKind.Object
                ? arguments.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            using JsonDocument response = await SendJsonRpcAsync(
                server,
                session,
                "tools/call",
                new Dictionary<string, object?>
                {
                    ["name"] = alias.ToolName,
                    ["arguments"] = callArguments
                },
                cancellationToken);

            if (TryGetRpcError(response.RootElement, out string error))
            {
                return $"MCP tool failed: {RedactServerSecrets(server, error)}";
            }

            if (!TryGetResult(response.RootElement, out var result))
            {
                return "MCP tool returned no result.";
            }

            return RedactServerSecrets(server, FormatToolCallResult(alias, result));
        }

        private async Task<string> ExecuteBuiltInToolAsync(
            AgentMcpToolAlias alias,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (alias.Alias.Equals(BuiltInComfyUiAlias, StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteComfyUiGenerateImageAsync(arguments, cancellationToken);
            }

            return $"MCP tool failed: unknown built-in MCP tool alias: {alias.Alias}";
        }

        private async Task<string> ExecuteComfyUiGenerateImageAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            string apiJson = GetJsonArgument(arguments, "apiJson", "api_json", "workflowJson", "workflow_json", "promptJson", "prompt_json");
            if (string.IsNullOrWhiteSpace(apiJson))
            {
                return "MCP tool failed: ComfyUI apiJson is empty.";
            }

            string outputPath = AgentToolHelpers.GetFirstStringArgument(
                arguments,
                "outputFileName",
                "output_file_name",
                "fileName",
                "filename",
                "saveAs",
                "save_as",
                "outputPath",
                "output_path",
                "path");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return "MCP tool failed: ComfyUI outputFileName is empty.";
            }

            string endpoint = AgentToolHelpers.GetFirstStringArgument(arguments, "endpoint", "baseUrl", "base_url", "server", "url");
            endpoint = NormalizeComfyEndpoint(endpoint);
            string outputNodeId = AgentToolHelpers.GetFirstStringArgument(arguments, "outputNodeId", "output_node_id", "nodeId", "node_id");
            string promptText = AgentToolHelpers.GetFirstStringArgument(
                arguments,
                "prompt",
                "positivePrompt",
                "positive_prompt",
                "imagePrompt",
                "image_prompt",
                "text",
                "string_a");
            int timeoutSeconds = Math.Max(1, AgentToolHelpers.GetIntArgument(arguments, "timeoutSeconds", DefaultComfyUiTimeoutSeconds));
            int pollIntervalMs = Math.Clamp(
                AgentToolHelpers.GetIntArgument(arguments, "pollIntervalMs", DefaultComfyUiPollIntervalMs),
                250,
                10_000);

            JsonObject parameters = ExtractJsonObjectArgument(arguments, "parameters", "params");
            IReadOnlyList<ComfyInputImageRef> inputImages = await UploadComfyInputImagesAsync(
                endpoint,
                ExtractComfyInputImagePaths(arguments),
                cancellationToken);
            JsonObject promptPayload = BuildComfyPromptPayload(apiJson, parameters, promptText, inputImages);
            using JsonDocument promptResponse = await PostComfyJsonAsync(endpoint, "prompt", promptPayload, cancellationToken);
            string promptId = TryGetStringProperty(promptResponse.RootElement, "prompt_id");
            if (string.IsNullOrWhiteSpace(promptId))
            {
                return "MCP tool failed: ComfyUI did not return prompt_id.";
            }

            ComfyImageRef image = await WaitForComfyImageAsync(
                endpoint,
                promptId,
                outputNodeId,
                TimeSpan.FromSeconds(timeoutSeconds),
                pollIntervalMs,
                cancellationToken);
            byte[] imageBytes = await DownloadComfyImageAsync(endpoint, image, cancellationToken);
            string fullPath = ResolveComfyOutputPath(outputPath, image);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(fullPath, imageBytes, cancellationToken);
            if (_fileModifiedAsync != null)
            {
                await _fileModifiedAsync(fullPath);
            }

            string root = ResolveWorkspaceRoot();
            string displayPath = AgentWorkspaceFileResolver.IsInsideRoot(root, fullPath)
                ? AgentWorkspaceFileResolver.RelativePath(root, fullPath)
                : fullPath;
            string uploadedText = inputImages.Count == 0
                ? string.Empty
                : "\ninput_images: " + string.Join(", ", inputImages.Select(item => item.FileName));
            return $"MCP tool result: ComfyUI image saved: {displayPath}\nprompt_id: {promptId}\nsource_image: {image.FileName}{uploadedText}";
        }

        private async Task RefreshServerToolsAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            try
            {
                _serverStatus[server.Id] = _getString("AgentMcpStatusConnecting", "연결 중");
                UpdateUI();
                AgentMcpSession session = await EnsureSessionAsync(server, cancellationToken, forceRefresh: true);
                session.Tools.Clear();

                string? cursor = null;
                do
                {
                    Dictionary<string, object?> parameters = new();
                    if (!string.IsNullOrEmpty(cursor))
                    {
                        parameters["cursor"] = cursor;
                    }

                    using JsonDocument response = await SendJsonRpcAsync(server, session, "tools/list", parameters, cancellationToken);
                    if (TryGetRpcError(response.RootElement, out string error))
                    {
                        throw new InvalidOperationException(error);
                    }

                    if (!TryGetResult(response.RootElement, out var result))
                    {
                        break;
                    }

                    if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tool in tools.EnumerateArray())
                        {
                            string name = TryGetStringProperty(tool, "name");
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                continue;
                            }

                            session.Tools.Add(new AgentMcpTool
                            {
                                Name = name,
                                Description = TryGetStringProperty(tool, "description"),
                                InputSchemaJson = TryGetPropertyRawJson(tool, "inputSchema", "{}")
                            });
                        }
                    }

                    cursor = TryGetStringProperty(result, "nextCursor");
                }
                while (!string.IsNullOrEmpty(cursor));

                _serverStatus[server.Id] = string.Format(
                    _getString("AgentMcpStatusToolCount", "{0:N0}개 도구"),
                    session.Tools.Count);
            }
            catch (Exception ex)
            {
                _serverStatus[server.Id] = string.Format(
                    _getString("AgentMcpStatusFailedFormat", "연결 실패: {0}"),
                    ex.Message);
            }

            RebuildAliases();
            UpdateUI();
        }

        private async Task<AgentMcpSession> EnsureSessionAsync(
            AgentMcpServer server,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            if (!forceRefresh && _sessions.TryGetValue(server.Id, out var existing) && existing.Initialized)
            {
                return existing;
            }

            var session = new AgentMcpSession();
            _sessions[server.Id] = session;

            using JsonDocument initializeResponse = await SendJsonRpcAsync(
                server,
                session,
                "initialize",
                new Dictionary<string, object?>
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new Dictionary<string, object?>(),
                    ["clientInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "TxtAIEditor",
                        ["version"] = "1.0"
                    }
                },
                cancellationToken);

            if (TryGetRpcError(initializeResponse.RootElement, out string error))
            {
                throw new InvalidOperationException(error);
            }

            await SendJsonRpcNotificationAsync(server, session, "notifications/initialized", cancellationToken);
            session.Initialized = true;
            return session;
        }

        private async Task<JsonDocument> SendJsonRpcAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            int id = session.NextId++;
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>()
            };

            string responseText = await PostAsync(server, session, payload, cancellationToken);
            return ParseRpcResponse(responseText);
        }

        private async Task SendJsonRpcNotificationAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            string method,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            };

            try
            {
                await PostAsync(server, session, payload, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Some MCP servers return 202/empty for notifications; others close without a JSON body.
            }
        }

        private async Task<string> PostAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            object payload,
            CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (server.AuthType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var header in server.Headers)
                {
                    string headerValue = GetHeaderSecret(server, header.Key, header.Value);
                    if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(headerValue))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, headerValue);
                    }
                }
            }
            else if (IsOAuthAuthType(server.AuthType))
            {
                string accessToken = await EnsureOAuthAccessTokenAsync(server, cancellationToken);
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }
            }

            if (!string.IsNullOrEmpty(session.SessionId))
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session.SessionId);
            }

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
            {
                session.SessionId = sessionIds.FirstOrDefault() ?? session.SessionId;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(RedactServerSecrets(server, $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}"));
            }

            return body;
        }

        private static JsonDocument ParseRpcResponse(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                throw new InvalidOperationException("MCP server returned an empty response.");
            }

            string trimmed = responseText.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return JsonDocument.Parse(responseText);
            }

            var dataBuilder = new StringBuilder();
            using var reader = new StringReader(responseText);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string data = line.Substring(5).TrimStart();
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                dataBuilder.Append(data);
            }

            string dataText = dataBuilder.ToString();
            if (string.IsNullOrWhiteSpace(dataText))
            {
                throw new InvalidOperationException("MCP server returned no JSON-RPC data.");
            }

            return JsonDocument.Parse(dataText);
        }

        private static string GetJsonArgument(JsonElement arguments, params string[] names)
        {
            if (arguments.ValueKind == JsonValueKind.String)
            {
                return arguments.GetString() ?? string.Empty;
            }

            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (string name in names)
            {
                if (!arguments.TryGetProperty(name, out var value))
                {
                    continue;
                }

                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                    _ => string.Empty
                };
            }

            return string.Empty;
        }

        private static JsonObject ExtractJsonObjectArgument(JsonElement arguments, params string[] names)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return new JsonObject();
            }

            foreach (string name in names)
            {
                if (!arguments.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Object)
                {
                    return JsonNode.Parse(value.GetRawText()) as JsonObject ?? new JsonObject();
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    string json = value.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return new JsonObject();
                    }

                    return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
                }
            }

            return new JsonObject();
        }

        private static JsonObject BuildComfyPromptPayload(
            string apiJson,
            JsonObject parameters,
            string promptText,
            IReadOnlyList<ComfyInputImageRef> inputImages)
        {
            JsonNode workflowNode = JsonNode.Parse(apiJson) ??
                throw new InvalidOperationException("ComfyUI apiJson is not valid JSON.");

            if (workflowNode is not JsonObject workflowObject)
            {
                throw new InvalidOperationException("ComfyUI apiJson must be a JSON object.");
            }

            string clientId = Guid.NewGuid().ToString("N");
            if (workflowObject.TryGetPropertyValue("prompt", out JsonNode? promptNode) && promptNode != null)
            {
                ApplyComfyParameters(promptNode, parameters);
                ApplyComfyPromptText(promptNode, promptText);
                ApplyComfyInputImages(promptNode, inputImages);
                if (!workflowObject.ContainsKey("client_id"))
                {
                    workflowObject["client_id"] = clientId;
                }

                return workflowObject;
            }

            ApplyComfyParameters(workflowObject, parameters);
            ApplyComfyPromptText(workflowObject, promptText);
            ApplyComfyInputImages(workflowObject, inputImages);
            return new JsonObject
            {
                ["prompt"] = workflowObject,
                ["client_id"] = clientId
            };
        }

        private static int ApplyComfyParameters(JsonNode workflowNode, JsonObject parameters)
        {
            if (parameters.Count == 0)
            {
                return 0;
            }

            int replacementCount = ReplaceComfyPlaceholders(workflowNode, parameters);
            foreach (var parameter in parameters.ToList())
            {
                if (!parameter.Key.Contains('.', StringComparison.Ordinal))
                {
                    continue;
                }

                if (TrySetJsonPath(workflowNode, parameter.Key, parameter.Value))
                {
                    replacementCount++;
                }
            }

            return replacementCount;
        }

        private static bool ApplyComfyPromptText(JsonNode workflowNode, string promptText)
        {
            if (string.IsNullOrWhiteSpace(promptText) || workflowNode is not JsonObject workflowObject)
            {
                return false;
            }

            foreach (var node in workflowObject)
            {
                if (node.Value is not JsonObject nodeObject ||
                    !IsPositiveClipTextEncodeNode(nodeObject))
                {
                    continue;
                }

                if (TryApplyPromptToClipTextInput(workflowObject, nodeObject, promptText))
                {
                    return true;
                }
            }

            foreach (var node in workflowObject)
            {
                if (node.Value is JsonObject nodeObject &&
                    TryApplyPromptToStringA(nodeObject, promptText))
                {
                    return true;
                }
            }

            foreach (var node in workflowObject)
            {
                if (node.Value is JsonObject nodeObject &&
                    !IsNegativePromptNode(nodeObject) &&
                    TryApplyPromptToTextInput(nodeObject, promptText))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ApplyComfyInputImages(JsonNode workflowNode, IReadOnlyList<ComfyInputImageRef> inputImages)
        {
            if (inputImages.Count == 0 || workflowNode is not JsonObject workflowObject)
            {
                return 0;
            }

            int imageIndex = 0;
            foreach (var node in workflowObject)
            {
                if (imageIndex >= inputImages.Count)
                {
                    return imageIndex;
                }

                if (node.Value is JsonObject nodeObject &&
                    IsLoadImageNode(nodeObject) &&
                    TryApplyInputImageToNode(nodeObject, inputImages[imageIndex]))
                {
                    imageIndex++;
                }
            }

            foreach (var node in workflowObject)
            {
                if (imageIndex >= inputImages.Count)
                {
                    return imageIndex;
                }

                if (node.Value is JsonObject nodeObject &&
                    !IsLoadImageNode(nodeObject) &&
                    TryApplyInputImageToNode(nodeObject, inputImages[imageIndex]))
                {
                    imageIndex++;
                }
            }

            return imageIndex;
        }

        private static bool TryApplyInputImageToNode(JsonObject nodeObject, ComfyInputImageRef image)
        {
            if (!TryGetInputsObject(nodeObject, out var inputs) ||
                !inputs.TryGetPropertyValue("image", out JsonNode? imageNode) ||
                !IsEmptyJsonString(imageNode))
            {
                return false;
            }

            inputs["image"] = JsonValue.Create(image.FileName);
            return true;
        }

        private static bool IsLoadImageNode(JsonObject nodeObject)
        {
            string classType = GetJsonObjectString(nodeObject, "class_type");
            string title = GetNodeTitle(nodeObject);
            return classType.Contains("LoadImage", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Load Image", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryApplyPromptToClipTextInput(JsonObject workflowObject, JsonObject clipNode, string promptText)
        {
            if (!TryGetInputsObject(clipNode, out var inputs) ||
                !inputs.TryGetPropertyValue("text", out JsonNode? textNode))
            {
                return false;
            }

            if (textNode is JsonArray link &&
                link.Count > 0 &&
                TryGetJsonString(link[0], out string linkedNodeId) &&
                !string.IsNullOrWhiteSpace(linkedNodeId) &&
                workflowObject.TryGetPropertyValue(linkedNodeId, out JsonNode? linkedNode) &&
                linkedNode is JsonObject linkedObject)
            {
                return TryApplyPromptToStringA(linkedObject, promptText) ||
                    TryApplyPromptToTextInput(linkedObject, promptText);
            }

            return TryApplyPromptToTextInput(clipNode, promptText);
        }

        private static bool TryApplyPromptToStringA(JsonObject nodeObject, string promptText)
        {
            if (!TryGetInputsObject(nodeObject, out var inputs) ||
                !inputs.TryGetPropertyValue("string_a", out JsonNode? stringANode) ||
                !IsEmptyJsonString(stringANode))
            {
                return false;
            }

            inputs["string_a"] = JsonValue.Create(promptText);
            return true;
        }

        private static bool TryApplyPromptToTextInput(JsonObject nodeObject, string promptText)
        {
            if (!TryGetInputsObject(nodeObject, out var inputs) ||
                !inputs.TryGetPropertyValue("text", out JsonNode? textNode) ||
                !IsEmptyJsonString(textNode))
            {
                return false;
            }

            inputs["text"] = JsonValue.Create(promptText);
            return true;
        }

        private static bool TryGetInputsObject(JsonObject nodeObject, out JsonObject inputs)
        {
            inputs = null!;
            if (!nodeObject.TryGetPropertyValue("inputs", out JsonNode? inputsNode) ||
                inputsNode is not JsonObject inputsObject)
            {
                return false;
            }

            inputs = inputsObject;
            return true;
        }

        private static bool IsPositiveClipTextEncodeNode(JsonObject nodeObject)
        {
            string classType = GetJsonObjectString(nodeObject, "class_type");
            if (!classType.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string title = GetNodeTitle(nodeObject);
            if (title.Contains("negative", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return title.Contains("positive", StringComparison.OrdinalIgnoreCase) ||
                TryGetInputsObject(nodeObject, out var inputs) &&
                inputs.ContainsKey("text");
        }

        private static bool IsNegativePromptNode(JsonObject nodeObject)
        {
            string title = GetNodeTitle(nodeObject);
            string classType = GetJsonObjectString(nodeObject, "class_type");
            return title.Contains("negative", StringComparison.OrdinalIgnoreCase) ||
                classType.Contains("negative", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetNodeTitle(JsonObject nodeObject)
        {
            if (nodeObject.TryGetPropertyValue("_meta", out JsonNode? metaNode) &&
                metaNode is JsonObject metaObject)
            {
                return GetJsonObjectString(metaObject, "title");
            }

            return string.Empty;
        }

        private static string GetJsonObjectString(JsonObject obj, string propertyName)
        {
            return obj.TryGetPropertyValue(propertyName, out JsonNode? value) &&
                TryGetJsonString(value, out string text)
                    ? text ?? string.Empty
                    : string.Empty;
        }

        private static bool IsEmptyJsonString(JsonNode? node)
        {
            return TryGetJsonString(node, out string value) &&
                string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetJsonString(JsonNode? node, out string value)
        {
            value = string.Empty;
            if (node is not JsonValue jsonValue ||
                !jsonValue.TryGetValue<string>(out string? text))
            {
                return false;
            }

            value = text ?? string.Empty;
            return true;
        }

        private static int ReplaceComfyPlaceholders(JsonNode? node, JsonObject parameters)
        {
            if (node is JsonObject obj)
            {
                int count = 0;
                foreach (var property in obj.ToList())
                {
                    count += ReplaceChildPlaceholder(obj, property.Key, property.Value, parameters);
                }

                return count;
            }

            if (node is JsonArray array)
            {
                int count = 0;
                for (int i = 0; i < array.Count; i++)
                {
                    count += ReplaceArrayPlaceholder(array, i, array[i], parameters);
                }

                return count;
            }

            return 0;
        }

        private static int ReplaceChildPlaceholder(JsonObject parent, string key, JsonNode? child, JsonObject parameters)
        {
            if (TryBuildPlaceholderReplacement(child, parameters, out JsonNode? replacement))
            {
                parent[key] = replacement;
                return 1;
            }

            return ReplaceComfyPlaceholders(child, parameters);
        }

        private static int ReplaceArrayPlaceholder(JsonArray parent, int index, JsonNode? child, JsonObject parameters)
        {
            if (TryBuildPlaceholderReplacement(child, parameters, out JsonNode? replacement))
            {
                parent[index] = replacement;
                return 1;
            }

            return ReplaceComfyPlaceholders(child, parameters);
        }

        private static bool TryBuildPlaceholderReplacement(JsonNode? node, JsonObject parameters, out JsonNode? replacement)
        {
            replacement = null;
            if (node == null ||
                !TryGetJsonString(node, out string text) ||
                string.IsNullOrEmpty(text))
            {
                return false;
            }

            string replacedText = text;
            bool changed = false;
            foreach (var parameter in parameters)
            {
                string placeholder = "{{" + parameter.Key + "}}";
                if (!text.Contains(placeholder, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(text, placeholder, StringComparison.Ordinal))
                {
                    replacement = parameter.Value?.DeepClone();
                    return true;
                }

                replacedText = replacedText.Replace(
                    placeholder,
                    ConvertParameterToString(parameter.Value),
                    StringComparison.Ordinal);
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            replacement = JsonValue.Create(replacedText);
            return true;
        }

        private static bool TrySetJsonPath(JsonNode root, string path, JsonNode? value)
        {
            string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            JsonNode? current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                current = GetPathChild(current, segments[i]);
                if (current == null)
                {
                    return false;
                }
            }

            return SetPathChild(current, segments[^1], value?.DeepClone());
        }

        private static JsonNode? GetPathChild(JsonNode? node, string segment)
        {
            if (node is JsonObject obj)
            {
                return obj.TryGetPropertyValue(segment, out JsonNode? value) ? value : null;
            }

            if (node is JsonArray array &&
                int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) &&
                index >= 0 &&
                index < array.Count)
            {
                return array[index];
            }

            return null;
        }

        private static bool SetPathChild(JsonNode? node, string segment, JsonNode? value)
        {
            if (node is JsonObject obj)
            {
                obj[segment] = value;
                return true;
            }

            if (node is JsonArray array &&
                int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) &&
                index >= 0 &&
                index < array.Count)
            {
                array[index] = value;
                return true;
            }

            return false;
        }

        private static string ConvertParameterToString(JsonNode? value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return TryGetJsonString(value, out string text)
                ? text ?? string.Empty
                : value.ToJsonString();
        }

        private async Task<IReadOnlyList<ComfyInputImageRef>> UploadComfyInputImagesAsync(
            string endpoint,
            IReadOnlyList<string> imagePaths,
            CancellationToken cancellationToken)
        {
            if (imagePaths.Count == 0)
            {
                return Array.Empty<ComfyInputImageRef>();
            }

            var uploadedImages = new List<ComfyInputImageRef>();
            foreach (string imagePath in imagePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = ResolveComfyInputImagePath(imagePath);
                uploadedImages.Add(await UploadComfyInputImageAsync(endpoint, fullPath, cancellationToken));
            }

            return uploadedImages;
        }

        private async Task<ComfyInputImageRef> UploadComfyInputImageAsync(
            string endpoint,
            string fullPath,
            CancellationToken cancellationToken)
        {
            string fileName = Path.GetFileName(fullPath);
            using var content = new MultipartFormDataContent();
            await using var fileStream = File.OpenRead(fullPath);
            using var imageContent = new StreamContent(fileStream);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(GetImageMediaType(fullPath));
            content.Add(imageContent, "image", fileName);
            content.Add(new StringContent("input"), "type");
            content.Add(new StringContent("true"), "overwrite");

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildComfyUrl(endpoint, "upload/image"))
            {
                Content = content
            };
            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"ComfyUI input image upload failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            string uploadedName = fileName;
            string uploadedSubfolder = string.Empty;
            string uploadedType = "input";
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using JsonDocument uploadResponse = JsonDocument.Parse(body);
                    uploadedName = TryGetStringProperty(uploadResponse.RootElement, "name");
                    uploadedSubfolder = TryGetStringProperty(uploadResponse.RootElement, "subfolder");
                    uploadedType = TryGetStringProperty(uploadResponse.RootElement, "type");
                }
                catch (JsonException)
                {
                }
            }

            return new ComfyInputImageRef
            {
                FileName = string.IsNullOrWhiteSpace(uploadedName) ? fileName : uploadedName,
                LocalPath = fullPath,
                Subfolder = uploadedSubfolder,
                Type = string.IsNullOrWhiteSpace(uploadedType) ? "input" : uploadedType
            };
        }

        private string ResolveComfyInputImagePath(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                throw new InvalidOperationException("ComfyUI input image path is empty.");
            }

            string path = requestedPath.Trim();
            string fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(ResolveWorkspaceRoot(), path));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("ComfyUI input image file was not found.", requestedPath);
            }

            return fullPath;
        }

        private static IReadOnlyList<string> ExtractComfyInputImagePaths(JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            var paths = new List<string>();
            foreach (string name in new[]
            {
                "inputImagePath",
                "input_image_path",
                "imagePath",
                "image_path",
                "initImagePath",
                "init_image_path",
                "sourceImage",
                "source_image",
                "inputImage",
                "input_image"
            })
            {
                AddComfyInputImageArgument(arguments, name, paths);
            }

            foreach (string name in new[]
            {
                "inputImages",
                "input_images",
                "imagePaths",
                "image_paths",
                "images",
                "sourceImages",
                "source_images"
            })
            {
                AddComfyInputImageArgument(arguments, name, paths);
            }

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddComfyInputImageArgument(JsonElement arguments, string propertyName, List<string> paths)
        {
            if (!arguments.TryGetProperty(propertyName, out JsonElement value))
            {
                return;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string path = value.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
                return;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                string path = TryGetStringProperty(value, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = TryGetStringProperty(value, "file");
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
                return;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string path = item.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    string path = TryGetStringProperty(item, "path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        path = TryGetStringProperty(item, "file");
                    }

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(path);
                    }
                }
            }
        }

        private static string GetImageMediaType(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".tif" or ".tiff" => "image/tiff",
                _ => "image/png"
            };
        }

        private async Task<JsonDocument> PostComfyJsonAsync(
            string endpoint,
            string route,
            JsonObject payload,
            CancellationToken cancellationToken)
        {
            string json = payload.ToJsonString();
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildComfyUrl(endpoint, route))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"ComfyUI {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            return JsonDocument.Parse(body);
        }

        private async Task<ComfyImageRef> WaitForComfyImageAsync(
            string endpoint,
            string promptId,
            string outputNodeId,
            TimeSpan timeout,
            int pollIntervalMs,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
            string historyRoute = "history/" + Uri.EscapeDataString(promptId);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildComfyUrl(endpoint, historyRoute));
                using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
                {
                    using JsonDocument history = JsonDocument.Parse(body);
                    if (TryGetComfyImageFromHistory(history.RootElement, promptId, outputNodeId, out var image))
                    {
                        return image;
                    }
                }

                await Task.Delay(pollIntervalMs, cancellationToken);
            }

            throw new TimeoutException($"ComfyUI image generation timed out after {timeout.TotalSeconds:N0} seconds.");
        }

        private static bool TryGetComfyImageFromHistory(
            JsonElement root,
            string promptId,
            string outputNodeId,
            out ComfyImageRef image)
        {
            image = new ComfyImageRef();
            JsonElement entry = root;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(promptId, out JsonElement promptEntry))
            {
                entry = promptEntry;
            }

            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("outputs", out JsonElement outputs) ||
                outputs.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var output in outputs.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(outputNodeId) &&
                    !output.Name.Equals(outputNodeId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryGetComfyImageFromOutput(output.Value, out image))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetComfyImageFromOutput(JsonElement output, out ComfyImageRef image)
        {
            image = new ComfyImageRef();
            if (output.ValueKind != JsonValueKind.Object ||
                !output.TryGetProperty("images", out JsonElement images) ||
                images.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in images.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string fileName = TryGetStringProperty(item, "filename");
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                image = new ComfyImageRef
                {
                    FileName = fileName,
                    Subfolder = TryGetStringProperty(item, "subfolder"),
                    Type = TryGetStringProperty(item, "type")
                };
                return true;
            }

            return false;
        }

        private async Task<byte[]> DownloadComfyImageAsync(string endpoint, ComfyImageRef image, CancellationToken cancellationToken)
        {
            string query =
                "filename=" + Uri.EscapeDataString(image.FileName) +
                "&type=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(image.Type) ? "output" : image.Type) +
                "&subfolder=" + Uri.EscapeDataString(image.Subfolder ?? string.Empty);
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildComfyUrl(endpoint, "view?" + query));
            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string body = Encoding.UTF8.GetString(bytes);
                throw new InvalidOperationException($"ComfyUI image download failed: {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            return bytes;
        }

        private string ResolveComfyOutputPath(string requestedPath, ComfyImageRef image)
        {
            string path = requestedPath.Trim();
            if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            {
                path = Path.Combine(path, Path.GetFileName(image.FileName));
            }

            string extension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(extension))
            {
                string sourceExtension = Path.GetExtension(image.FileName);
                path += string.IsNullOrWhiteSpace(sourceExtension) ? ".png" : sourceExtension;
            }

            string root = ResolveWorkspaceRoot();
            string fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));

            if (!IsAllowedComfyOutputPath(root, fullPath))
            {
                throw new InvalidOperationException("ComfyUI output path must stay inside the workspace, C:\\tmp, or the system temp directory.");
            }

            return fullPath;
        }

        private string ResolveWorkspaceRoot()
        {
            string root = _workspaceRootProvider();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsAllowedComfyOutputPath(string workspaceRoot, string fullPath)
        {
            if (AgentWorkspaceFileResolver.IsInsideRoot(workspaceRoot, fullPath))
            {
                return true;
            }

            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (AgentWorkspaceFileResolver.IsInsideRoot(tempRoot, fullPath))
            {
                return true;
            }

            string cTmpRoot = Path.GetFullPath(@"C:\tmp").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return AgentWorkspaceFileResolver.IsInsideRoot(cTmpRoot, fullPath);
        }

        private static string NormalizeComfyEndpoint(string endpoint)
        {
            string normalized = string.IsNullOrWhiteSpace(endpoint)
                ? DefaultComfyUiEndpoint
                : endpoint.Trim();
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("ComfyUI endpoint must be an http or https URL.");
            }

            return normalized.TrimEnd('/');
        }

        private static Uri BuildComfyUrl(string endpoint, string route)
        {
            return new Uri(NormalizeComfyEndpoint(endpoint) + "/" + route.TrimStart('/'));
        }

        private static bool IsBuiltInComfyUiName(string serverName)
        {
            return serverName.Equals(BuiltInComfyUiName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBuiltInMcpId(string serverId)
        {
            return serverId.Equals(BuiltInComfyUiId, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsBuiltInComfyUiSelected()
        {
            return _selectedServerIds.Contains(BuiltInComfyUiId);
        }

        private void RebuildAliases()
        {
            _toolAliases.Clear();
            var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (IsBuiltInComfyUiSelected())
            {
                usedAliases.Add(BuiltInComfyUiAlias);
                _toolAliases[BuiltInComfyUiAlias] = new AgentMcpToolAlias
                {
                    Alias = BuiltInComfyUiAlias,
                    ServerId = BuiltInComfyUiId,
                    ServerName = BuiltInComfyUiName,
                    ToolName = BuiltInComfyUiToolName,
                    Description = "Generate an image through a local or remote ComfyUI HTTP API workflow, then download and save the produced image file.",
                    InputSchemaJson = BuiltInComfyUiInputSchemaJson,
                    IsBuiltIn = true
                };
            }

            foreach (var server in GetSelectedServers())
            {
                if (!_sessions.TryGetValue(server.Id, out var session))
                {
                    continue;
                }

                foreach (var tool in session.Tools)
                {
                    string baseAlias = "mcp_" + SanitizeToolSegment(server.Name) + "_" + SanitizeToolSegment(tool.Name);
                    string alias = baseAlias;
                    int suffix = 2;
                    while (!usedAliases.Add(alias))
                    {
                        alias = baseAlias + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        suffix++;
                    }

                    _toolAliases[alias] = new AgentMcpToolAlias
                    {
                        Alias = alias,
                        ServerId = server.Id,
                        ServerName = server.Name,
                        ToolName = tool.Name,
                        Description = string.IsNullOrWhiteSpace(tool.Description) ? "(No description provided.)" : tool.Description,
                        InputSchemaJson = string.IsNullOrWhiteSpace(tool.InputSchemaJson) ? "{}" : tool.InputSchemaJson
                    };
                }
            }
        }

        private void UpdateUI()
        {
            var items = new List<AgentMcpItem>
            {
                new AgentMcpItem
                {
                    Name = BuiltInComfyUiName,
                    Endpoint = DefaultComfyUiEndpoint,
                    Detail = _getString("AgentMcpComfyUiDetail", "내장 플러그인 - API JSON/파라미터로 이미지 생성"),
                    IsSelected = IsBuiltInComfyUiSelected(),
                    IsBuiltIn = true,
                    CanEdit = false,
                    CanDelete = false
                }
            };

            items.AddRange(_servers
                .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(server =>
                {
                    string detail = _serverStatus.TryGetValue(server.Id, out string? status) && !string.IsNullOrWhiteSpace(status)
                        ? $"{server.Endpoint} - {status}"
                        : server.Endpoint;
                    if (server.Headers.Count > 0)
                    {
                        detail += $" - headers: {string.Join(", ", server.Headers.Keys)}";
                    }

                    return new AgentMcpItem
                    {
                        Name = server.Name,
                        Endpoint = server.Endpoint,
                        Detail = detail,
                        IsSelected = _selectedServerIds.Contains(server.Id)
                    };
                })
                .ToList());
            var selectedNames = new List<string>();
            if (IsBuiltInComfyUiSelected())
            {
                selectedNames.Add(BuiltInComfyUiName);
            }

            selectedNames.AddRange(GetSelectedServers().Select(server => server.Name));

            void ApplyUI()
            {
                _agentPane.UpdateAgentMcpMenu(items, selectedNames, _getString);
                QueueContextChanged();
            }

            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.HasThreadAccess == true)
            {
                ApplyUI();
                return;
            }

            if (dispatcher?.TryEnqueue(ApplyUI) == true)
            {
                return;
            }

            ApplyUI();
        }

        private void QueueContextChanged()
        {
            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _contextChanged()) == true)
            {
                return;
            }

            _contextChanged();
        }

        private async Task SaveAsync()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_mcpFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(_servers, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_mcpFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save MCP servers: {ex.Message}");
            }

            RebuildAliases();
            UpdateUI();
        }

        private static List<AgentMcpServer> DeserializeImportedServers(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<AgentMcpServer>>(json) ?? new List<AgentMcpServer>();
            }

            var servers = new List<AgentMcpServer>();
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("mcpServers", out var mcpServers) ||
                mcpServers.ValueKind != JsonValueKind.Object)
            {
                return servers;
            }

            foreach (var property in mcpServers.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string endpoint = TryGetStringProperty(property.Value, "url");
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    endpoint = TryGetStringProperty(property.Value, "endpoint");
                }

                servers.Add(new AgentMcpServer
                {
                    Name = property.Name,
                    Endpoint = endpoint,
                    Headers = ReadHeadersProperty(property.Value)
                });
            }

            return servers;
        }

        private static Dictionary<string, string> ReadHeadersProperty(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("headers", out var headersElement) ||
                headersElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in headersElement.EnumerateObject())
            {
                string key = property.Name?.Trim() ?? string.Empty;
                string value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    headers[key] = value.Trim();
                }
            }

            return headers;
        }

        private static Dictionary<string, string> NormalizeHeaders(Dictionary<string, string>? headers)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
            {
                return normalized;
            }

            foreach (var header in headers)
            {
                string key = header.Key?.Trim() ?? string.Empty;
                string value = header.Value?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    normalized[key] = value;
                }
            }

            return normalized;
        }

        private bool TryBuildAuthSettings(
            string authType,
            string headerName,
            string apiKey,
            string oauthToken,
            string oauthClientId,
            string oauthClientSecret,
            string oauthAuthorizationEndpoint,
            string oauthTokenEndpoint,
            string oauthScopes,
            out AgentMcpAuthSettings settings,
            out string error)
        {
            settings = new AgentMcpAuthSettings
            {
                AuthType = NormalizeAuthType(authType, null)
            };
            error = string.Empty;
            headerName = headerName?.Trim() ?? string.Empty;
            apiKey = apiKey?.Trim() ?? string.Empty;
            oauthToken = oauthToken?.Trim() ?? string.Empty;
            oauthClientId = oauthClientId?.Trim() ?? string.Empty;
            oauthClientSecret = oauthClientSecret?.Trim() ?? string.Empty;
            oauthAuthorizationEndpoint = oauthAuthorizationEndpoint?.Trim() ?? string.Empty;
            oauthTokenEndpoint = oauthTokenEndpoint?.Trim() ?? string.Empty;
            oauthScopes = oauthScopes?.Trim() ?? string.Empty;

            if (settings.AuthType.Equals(AuthTypeNone, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (settings.AuthType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(apiKey))
                {
                    error = _getString("AgentMcpHeaderPairRequired", "API Key를 입력하려면 Header 이름도 함께 입력해주세요.");
                    return false;
                }

                settings.Headers[headerName] = apiKey;
                return true;
            }

            if (settings.AuthType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(oauthToken))
                {
                    error = _getString("AgentMcpOAuthTokenRequired", "OAuth Access Token을 입력해주세요.");
                    return false;
                }

                settings.OAuthAccessToken = oauthToken;
                return true;
            }

            if (settings.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(oauthClientId) ||
                    string.IsNullOrWhiteSpace(oauthClientSecret) ||
                    string.IsNullOrWhiteSpace(oauthAuthorizationEndpoint) ||
                    string.IsNullOrWhiteSpace(oauthTokenEndpoint))
                {
                    error = _getString("AgentMcpOAuthClientConfigRequired", "OAuth Client ID, Client Secret, Authorization URL, Token URL을 입력해주세요.");
                    return false;
                }

                if (!IsValidHttpEndpoint(oauthAuthorizationEndpoint) || !IsValidHttpEndpoint(oauthTokenEndpoint))
                {
                    error = _getString("AgentMcpOAuthEndpointInvalid", "OAuth Authorization URL과 Token URL은 http 또는 https URL이어야 합니다.");
                    return false;
                }

                settings.OAuthClientId = oauthClientId;
                settings.OAuthClientSecret = oauthClientSecret;
                settings.OAuthAuthorizationEndpoint = oauthAuthorizationEndpoint;
                settings.OAuthTokenEndpoint = oauthTokenEndpoint;
                settings.OAuthScopes = oauthScopes;
                return true;
            }

            error = _getString("AgentMcpAuthTypeInvalid", "지원하지 않는 인증 방식입니다.");
            return false;
        }

        private void ApplyAuthSettings(AgentMcpServer server, AgentMcpAuthSettings settings, bool deleteEmptySecrets)
        {
            server.AuthType = settings.AuthType;
            server.Headers = settings.AuthType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase)
                ? StoreHeaderSecrets(server, settings.Headers, deleteEmptySecrets)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!settings.AuthType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase))
            {
                DeleteCredentialsForRemovedHeaders(server, server.Headers);
            }

            server.OAuthClientId = settings.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase)
                ? settings.OAuthClientId
                : string.Empty;
            server.OAuthAuthorizationEndpoint = settings.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase)
                ? settings.OAuthAuthorizationEndpoint
                : string.Empty;
            server.OAuthTokenEndpoint = settings.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase)
                ? settings.OAuthTokenEndpoint
                : string.Empty;
            server.OAuthScopes = settings.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase)
                ? settings.OAuthScopes
                : string.Empty;

            if (settings.AuthType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase))
            {
                server.OAuthAccessToken = StoreOAuthSecret(server, "access_token", settings.OAuthAccessToken, deleteEmptySecrets);
                server.OAuthRefreshToken = string.Empty;
                server.OAuthClientSecret = string.Empty;
                server.OAuthAccessTokenExpiresAt = DateTimeOffset.MaxValue;
            }
            else if (settings.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase))
            {
                server.OAuthAccessToken = StoreOAuthSecret(server, "access_token", settings.OAuthAccessToken, deleteEmptySecrets);
                server.OAuthRefreshToken = StoreOAuthSecret(server, "refresh_token", settings.OAuthRefreshToken, deleteEmptySecrets);
                server.OAuthClientSecret = StoreOAuthSecret(server, "client_secret", settings.OAuthClientSecret, deleteEmptySecrets);
                if (settings.OAuthAccessTokenExpiresAt != default)
                {
                    server.OAuthAccessTokenExpiresAt = settings.OAuthAccessTokenExpiresAt;
                }
            }
            else
            {
                server.OAuthAccessToken = string.Empty;
                server.OAuthRefreshToken = string.Empty;
                server.OAuthClientSecret = string.Empty;
                server.OAuthAccessTokenExpiresAt = default;
            }
        }

        private static string NormalizeAuthType(string authType, Dictionary<string, string>? headers)
        {
            authType = authType?.Trim() ?? string.Empty;
            if (authType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase) ||
                authType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase) ||
                authType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) ||
                authType.Equals(AuthTypeNone, StringComparison.OrdinalIgnoreCase))
            {
                return authType;
            }

            return headers != null && headers.Count > 0 ? AuthTypeApiKey : AuthTypeNone;
        }

        private static bool IsOAuthAuthType(string authType)
        {
            return authType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase) ||
                authType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> EnsureOAuthAccessTokenAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            if (server.AuthType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase))
            {
                return GetOAuthSecret(server, "access_token", server.OAuthAccessToken);
            }

            if (!server.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string accessToken = GetOAuthSecret(server, "access_token", server.OAuthAccessToken);
            if (!string.IsNullOrWhiteSpace(accessToken) &&
                server.OAuthAccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return accessToken;
            }

            string refreshToken = GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken);
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                try
                {
                    return await RefreshOAuthAccessTokenAsync(server, refreshToken, cancellationToken);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to refresh MCP OAuth token: {ex.Message}");
                }
            }

            return await RunBrowserOAuthLoginAsync(server, cancellationToken);
        }

        private async Task<string> RefreshOAuthAccessTokenAsync(AgentMcpServer server, string refreshToken, CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = server.OAuthClientId,
                ["client_secret"] = GetOAuthClientSecret(server)
            };
            OAuthTokenResponse token = await SendOAuthTokenRequestAsync(server.OAuthTokenEndpoint, form, cancellationToken);
            SaveOAuthTokenResponse(server, token, refreshToken);
            await SaveAsync();
            return token.AccessToken;
        }

        private async Task<string> RunBrowserOAuthLoginAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            string clientSecret = GetOAuthClientSecret(server);
            if (string.IsNullOrWhiteSpace(server.OAuthClientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(server.OAuthAuthorizationEndpoint) ||
                string.IsNullOrWhiteSpace(server.OAuthTokenEndpoint))
            {
                throw new InvalidOperationException(_getString("AgentMcpOAuthClientConfigRequired", "OAuth Client ID, Client Secret, Authorization URL, Token URL을 입력해주세요."));
            }

            int port = GetFreeTcpPort();
            string redirectUri = $"http://127.0.0.1:{port}/callback/";
            string state = CreateRandomBase64Url(32);
            string codeVerifier = CreateRandomBase64Url(64);
            string codeChallenge = CreateCodeChallenge(codeVerifier);
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            string authorizationUrl = BuildUrl(server.OAuthAuthorizationEndpoint, new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = server.OAuthClientId,
                ["redirect_uri"] = redirectUri,
                ["scope"] = server.OAuthScopes,
                ["state"] = state,
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
                ["access_type"] = "offline",
                ["prompt"] = "consent"
            });

            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true
            });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            Task<HttpListenerContext> contextTask = listener.GetContextAsync();
            Task completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));
            if (completed != contextTask)
            {
                throw new TimeoutException(_getString("AgentMcpOAuthLoginTimeout", "OAuth 브라우저 로그인이 시간 초과되었습니다."));
            }

            HttpListenerContext context = await contextTask;
            string? error = context.Request.QueryString["error"];
            string? code = context.Request.QueryString["code"];
            string? returnedState = context.Request.QueryString["state"];
            byte[] responseBytes = Encoding.UTF8.GetBytes("<html><body>OAuth login complete. You can close this window.</body></html>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, cancellationToken);
            context.Response.Close();

            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }

            if (string.IsNullOrWhiteSpace(code) || !string.Equals(state, returnedState, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(_getString("AgentMcpOAuthLoginInvalidResponse", "OAuth 로그인 응답이 올바르지 않습니다."));
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = server.OAuthClientId,
                ["client_secret"] = clientSecret,
                ["code_verifier"] = codeVerifier
            };
            OAuthTokenResponse token = await SendOAuthTokenRequestAsync(server.OAuthTokenEndpoint, form, cancellationToken);
            SaveOAuthTokenResponse(server, token, GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken));
            await SaveAsync();
            return token.AccessToken;
        }

        private async Task RunInitialOAuthLoginIfNeededAsync(AgentMcpServer? server, string errorTitle)
        {
            if (server == null ||
                !server.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken)))
            {
                return;
            }

            try
            {
                await RunBrowserOAuthLoginAsync(server, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _showError(errorTitle, ex.Message);
            }
        }

        private static async Task<OAuthTokenResponse> SendOAuthTokenRequestAsync(string tokenEndpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
        {
            using var content = new FormUrlEncodedContent(form.Where(item => !string.IsNullOrWhiteSpace(item.Value)));
            using HttpResponseMessage response = await HttpClient.PostAsync(tokenEndpoint, content, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {json}");
            }

            using JsonDocument document = JsonDocument.Parse(json);
            string accessToken = TryGetStringProperty(document.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("OAuth token response did not include an access token.");
            }

            int expiresIn = 3600;
            if (document.RootElement.TryGetProperty("expires_in", out var expiresElement) &&
                expiresElement.TryGetInt32(out int parsedExpiresIn))
            {
                expiresIn = parsedExpiresIn;
            }

            return new OAuthTokenResponse
            {
                AccessToken = accessToken,
                RefreshToken = TryGetStringProperty(document.RootElement, "refresh_token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn))
            };
        }

        private void SaveOAuthTokenResponse(AgentMcpServer server, OAuthTokenResponse token, string existingRefreshToken)
        {
            server.OAuthAccessToken = StoreOAuthSecret(server, "access_token", token.AccessToken, deleteEmptySecret: true);
            server.OAuthRefreshToken = StoreOAuthSecret(
                server,
                "refresh_token",
                string.IsNullOrWhiteSpace(token.RefreshToken) ? existingRefreshToken : token.RefreshToken,
                deleteEmptySecret: false);
            server.OAuthAccessTokenExpiresAt = token.ExpiresAt;
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string BuildUrl(string baseUrl, Dictionary<string, string> query)
        {
            var builder = new StringBuilder(baseUrl);
            builder.Append(baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?');
            builder.Append(string.Join("&", query
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}")));
            return builder.ToString();
        }

        private static string CreateRandomBase64Url(int byteCount)
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(byteCount);
            return Base64UrlEncode(bytes);
        }

        private static string CreateCodeChallenge(string codeVerifier)
        {
            byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
            return Base64UrlEncode(hash);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private Dictionary<string, string> StoreHeaderSecrets(
            AgentMcpServer server,
            Dictionary<string, string> headers,
            bool deleteEmptySecrets = false)
        {
            var storedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                string key = header.Key?.Trim() ?? string.Empty;
                string value = header.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                storedHeaders[key] = string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _credentialService.WriteCredential(
                        GetHeaderCredentialTarget(server, key),
                        "TxtAIEditor_mcp",
                        value);
                }
                else if (deleteEmptySecrets)
                {
                    _credentialService.DeleteCredential(GetHeaderCredentialTarget(server, key));
                }
            }

            return storedHeaders;
        }

        private void MoveInlineHeaderValuesToCredential(AgentMcpServer server)
        {
            server.Headers = StoreHeaderSecrets(server, NormalizeHeaders(server.Headers));
        }

        private void DeleteCredentialsForRemovedHeaders(AgentMcpServer server, Dictionary<string, string> nextHeaders)
        {
            var nextHeaderNames = new HashSet<string>(nextHeaders.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string oldHeaderName in server.Headers.Keys.ToList())
            {
                if (!nextHeaderNames.Contains(oldHeaderName))
                {
                    _credentialService.DeleteCredential(GetHeaderCredentialTarget(server, oldHeaderName));
                }
            }
        }

        private void DeleteAllCredentials(AgentMcpServer server)
        {
            foreach (string headerName in server.Headers.Keys.ToList())
            {
                _credentialService.DeleteCredential(GetHeaderCredentialTarget(server, headerName));
            }

            DeleteOAuthCredentials(server);
        }

        private string GetHeaderSecret(AgentMcpServer server, string headerName, string fallbackValue)
        {
            if (string.IsNullOrWhiteSpace(headerName))
            {
                return string.Empty;
            }

            string? credential = _credentialService.ReadCredential(GetHeaderCredentialTarget(server, headerName));
            if (!string.IsNullOrEmpty(credential))
            {
                return credential;
            }

            return fallbackValue ?? string.Empty;
        }

        private string StoreOAuthSecret(AgentMcpServer server, string secretName, string value, bool deleteEmptySecret)
        {
            value = value?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
            {
                _credentialService.WriteCredential(
                    GetOAuthCredentialTarget(server, secretName),
                    "TxtAIEditor_mcp",
                    value);
            }
            else if (deleteEmptySecret)
            {
                _credentialService.DeleteCredential(GetOAuthCredentialTarget(server, secretName));
            }

            return string.Empty;
        }

        private void MoveInlineOAuthSecretsToCredential(AgentMcpServer server)
        {
            server.OAuthAccessToken = StoreOAuthSecret(server, "access_token", server.OAuthAccessToken, deleteEmptySecret: false);
            server.OAuthRefreshToken = StoreOAuthSecret(server, "refresh_token", server.OAuthRefreshToken, deleteEmptySecret: false);
            server.OAuthClientSecret = StoreOAuthSecret(server, "client_secret", server.OAuthClientSecret, deleteEmptySecret: false);
        }

        private void DeleteOAuthCredentials(AgentMcpServer server)
        {
            _credentialService.DeleteCredential(GetOAuthCredentialTarget(server, "access_token"));
            _credentialService.DeleteCredential(GetOAuthCredentialTarget(server, "refresh_token"));
            _credentialService.DeleteCredential(GetOAuthCredentialTarget(server, "client_secret"));
        }

        private string GetOAuthSecret(AgentMcpServer server)
        {
            return GetOAuthSecret(server, "access_token", server.OAuthAccessToken);
        }

        private string GetOAuthClientSecret(AgentMcpServer server)
        {
            return GetOAuthSecret(server, "client_secret", server.OAuthClientSecret);
        }

        private string GetOAuthSecret(AgentMcpServer server, string secretName, string fallbackValue)
        {
            string? credential = _credentialService.ReadCredential(GetOAuthCredentialTarget(server, secretName));
            if (!string.IsNullOrEmpty(credential))
            {
                return credential;
            }

            return fallbackValue ?? string.Empty;
        }

        private string RedactServerSecrets(AgentMcpServer server, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var secrets = new List<string>();
            foreach (var header in server.Headers)
            {
                string secret = GetHeaderSecret(server, header.Key, header.Value);
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    secrets.Add(secret);
                }
            }

            AddSecretIfPresent(secrets, GetOAuthSecret(server, "access_token", server.OAuthAccessToken));
            AddSecretIfPresent(secrets, GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken));
            AddSecretIfPresent(secrets, GetOAuthSecret(server, "client_secret", server.OAuthClientSecret));

            string redacted = text;
            foreach (string secret in secrets.Distinct(StringComparer.Ordinal))
            {
                if (secret.Length < 4)
                {
                    continue;
                }

                redacted = redacted.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }

            return redacted;
        }

        private static void AddSecretIfPresent(List<string> secrets, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                secrets.Add(value);
            }
        }

        private static string GetHeaderCredentialTarget(AgentMcpServer server, string headerName)
        {
            return $"TxtAIEditor_MCP_{server.Id}_{SanitizeCredentialSegment(headerName)}";
        }

        private static string GetOAuthCredentialTarget(AgentMcpServer server, string secretName)
        {
            return $"TxtAIEditor_MCP_{server.Id}_oauth_{SanitizeCredentialSegment(secretName)}";
        }

        private static string SanitizeCredentialSegment(string value)
        {
            string normalized = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_.-]+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(normalized) ? "header" : normalized;
        }

        private AgentMcpServer? FindServer(string name)
        {
            return _servers.FirstOrDefault(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsValidHttpEndpoint(string endpoint)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private List<AgentMcpServer> GetSelectedServers()
        {
            return _servers
                .Where(server => _selectedServerIds.Contains(server.Id))
                .ToList();
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

        private TextBlock CreateLabel(string text)
        {
            return new TextBlock
            {
                Text = text
            };
        }

        private TextBox CreateTextBox(string placeholder)
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

        private PasswordBox CreatePasswordBox(string placeholder)
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

        private static string FormatToolCallResult(AgentMcpToolAlias alias, JsonElement result)
        {
            bool isError = result.TryGetProperty("isError", out var isErrorProp) && isErrorProp.ValueKind == JsonValueKind.True;
            var builder = new StringBuilder();
            builder.AppendLine(isError
                ? $"MCP tool returned an error: {alias.Alias}"
                : $"MCP tool result: {alias.Alias}");

            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    string type = TryGetStringProperty(item, "type");
                    if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = TryGetStringProperty(item, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            builder.AppendLine(text);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(type))
                    {
                        builder.AppendLine($"[{type} content omitted]");
                    }
                }
            }

            if (result.TryGetProperty("structuredContent", out var structuredContent))
            {
                builder.AppendLine("[structuredContent]");
                builder.AppendLine(structuredContent.GetRawText());
            }

            string textResult = builder.ToString().TrimEnd();
            return string.IsNullOrWhiteSpace(textResult)
                ? result.GetRawText()
                : textResult;
        }

        private static bool TryGetRpcError(JsonElement root, out string error)
        {
            error = string.Empty;
            if (!root.TryGetProperty("error", out var errorElement))
            {
                return false;
            }

            if (errorElement.ValueKind == JsonValueKind.Object)
            {
                string message = TryGetStringProperty(errorElement, "message");
                string code = TryGetPropertyRawJson(errorElement, "code", string.Empty);
                error = string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
            }
            else
            {
                error = errorElement.GetRawText();
            }

            return true;
        }

        private static bool TryGetResult(JsonElement root, out JsonElement result)
        {
            if (root.TryGetProperty("result", out result))
            {
                return true;
            }

            result = default;
            return false;
        }

        private static string TryGetStringProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return string.Empty;
            }

            return property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : property.GetRawText();
        }

        private static string TryGetPropertyRawJson(JsonElement element, string propertyName, string fallback)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return fallback;
            }

            return property.GetRawText();
        }

        private static string SanitizeToolSegment(string value)
        {
            string normalized = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_]+", "_").Trim('_').ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"_+", "_");
            return string.IsNullOrWhiteSpace(normalized) ? "server" : normalized;
        }

        private sealed class AgentMcpSession
        {
            public string SessionId { get; set; } = string.Empty;
            public bool Initialized { get; set; }
            public int NextId { get; set; } = 1;
            public List<AgentMcpTool> Tools { get; } = new();
        }

        private sealed class AgentMcpTool
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string InputSchemaJson { get; set; } = "{}";
        }

        private sealed class ComfyImageRef
        {
            public string FileName { get; set; } = string.Empty;
            public string Subfolder { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
        }

        private sealed class ComfyInputImageRef
        {
            public string FileName { get; set; } = string.Empty;
            public string LocalPath { get; set; } = string.Empty;
            public string Subfolder { get; set; } = string.Empty;
            public string Type { get; set; } = "input";
        }

        private sealed class AgentMcpAuthSettings
        {
            public string AuthType { get; set; } = AuthTypeNone;
            public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string OAuthAccessToken { get; set; } = string.Empty;
            public string OAuthRefreshToken { get; set; } = string.Empty;
            public string OAuthClientId { get; set; } = string.Empty;
            public string OAuthClientSecret { get; set; } = string.Empty;
            public string OAuthAuthorizationEndpoint { get; set; } = string.Empty;
            public string OAuthTokenEndpoint { get; set; } = string.Empty;
            public string OAuthScopes { get; set; } = string.Empty;
            public DateTimeOffset OAuthAccessTokenExpiresAt { get; set; }
        }

        private sealed class OAuthTokenResponse
        {
            public string AccessToken { get; set; } = string.Empty;
            public string RefreshToken { get; set; } = string.Empty;
            public DateTimeOffset ExpiresAt { get; set; }
        }
    }
}
