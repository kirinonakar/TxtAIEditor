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
    }

    internal sealed class AgentMcpToolAlias
    {
        public string Alias { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string InputSchemaJson { get; set; } = "{}";
    }

    internal sealed class AgentMcpController
    {
        private const string ProtocolVersion = "2025-06-18";
        private const string AuthTypeNone = "none";
        private const string AuthTypeApiKey = "apiKey";
        private const string AuthTypeOAuthBearer = "oauthBearer";
        private const string AuthTypeOAuthAuthorizationCode = "oauthAuthorizationCode";
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
            Action? beforeDialog,
            Action? afterDialog)
        {
            _agentPane = agentPane;
            _initializePickerWindow = initializePickerWindow;
            _credentialService = credentialService;
            _showError = showError;
            _getString = getString;
            _contextChanged = contextChanged;
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

            _selectedServerIds.RemoveWhere(id => _servers.All(server => !server.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
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
            return string.Join(", ", GetSelectedServers().Select(server => server.Name));
        }

        public async Task EnsureActiveToolsAsync(CancellationToken cancellationToken)
        {
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
            if (selectedServers.Count == 0)
            {
                return string.Empty;
            }

            RebuildAliases();
            var builder = new StringBuilder();
            builder.AppendLine("[Enabled MCP servers]");
            builder.AppendLine("These external tools are provided by Model Context Protocol servers. Use their exact alias names in tool_call.");
            builder.AppendLine("MCP tool aliases use the form mcp_server_tool and are executed through MCP tools/call.");
            builder.AppendLine();

            foreach (var server in selectedServers)
            {
                builder.AppendLine($"## {server.Name}");
                builder.AppendLine($"Endpoint: {server.Endpoint}");
                var aliases = _toolAliases.Values
                    .Where(alias => alias.ServerId.Equals(server.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(alias => alias.Alias, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (aliases.Count == 0)
                {
                    string status = _serverStatus.TryGetValue(server.Id, out string? value) ? value : "tools not loaded yet";
                    builder.AppendLine($"Status: {status}");
                    builder.AppendLine();
                    continue;
                }

                foreach (var alias in aliases)
                {
                    builder.AppendLine($"- {alias.Alias}: {alias.Description}");
                    builder.AppendLine($"  Original MCP tool: {alias.ToolName}");
                    builder.AppendLine($"  Arguments JSON schema: {alias.InputSchemaJson}");
                }

                builder.AppendLine();
            }

            return builder.ToString().Trim();
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
                return $"MCP tool failed: {error}";
            }

            if (!TryGetResult(response.RootElement, out var result))
            {
                return "MCP tool returned no result.";
            }

            return FormatToolCallResult(alias, result);
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
                throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
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

        private void RebuildAliases()
        {
            _toolAliases.Clear();
            var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var items = _servers
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
                .ToList();
            var selectedNames = GetSelectedServers().Select(server => server.Name).ToList();

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
