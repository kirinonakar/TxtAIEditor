using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using Windows.Storage.Pickers;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;

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
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly AgentPane _agentPane;
        private readonly Action<object> _initializePickerWindow;
        private readonly ISettingsService _settingsService;
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly AgentMcpOAuthService _oauthService;
        private readonly AgentMcpDialogService _dialogService;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Action _contextChanged;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly AgentMcpComfyUiTool _comfyUiTool;
        private readonly AgentMcpBrowserUseTool _browserUseTool;
        private readonly string _mcpFilePath;
        private readonly List<AgentMcpServer> _servers = new();
        private readonly HashSet<string> _selectedServerIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AgentMcpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AgentMcpToolAlias> _toolAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _serverStatus = new(StringComparer.OrdinalIgnoreCase);
        private string _comfyUiStatus = string.Empty;

        public AgentMcpController(
            AgentPane agentPane,
            Action<object> initializePickerWindow,
            ISettingsService settingsService,
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
            _settingsService = settingsService;
            _showError = showError;
            _getString = getString;
            _credentialStore = new AgentMcpCredentialStore(credentialService);
            _oauthService = new AgentMcpOAuthService(_credentialStore, _getString, SaveAsync);
            _contextChanged = contextChanged;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
            _dialogService = new AgentMcpDialogService(_agentPane, _initializePickerWindow, _getString, _beforeDialog, _afterDialog);
            _comfyUiTool = new AgentMcpComfyUiTool(workspaceRootProvider, () => _settingsService.CurrentSettings, fileModifiedAsync);
            _browserUseTool = new AgentMcpBrowserUseTool(() => _settingsService.CurrentSettings);

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
                                _credentialStore.MoveInlineHeaderValuesToCredential(s);
                                _credentialStore.MoveInlineOAuthSecretsToCredential(s);
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
                !_comfyUiTool.IsServerId(id) &&
                !_browserUseTool.IsServerId(id) &&
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
            AgentMcpDialogInput? input = await _dialogService.ShowAddAsync();
            if (input == null)
            {
                return;
            }

            string name = input.Name.Trim();
            string endpoint = input.Endpoint.Trim();
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

            string authType = input.AuthType;
            if (!TryBuildAuthSettings(
                authType,
                input.HeaderName,
                input.ApiKey,
                input.OAuthAccessToken,
                input.OAuthClientId,
                input.OAuthClientSecret,
                input.OAuthAuthorizationEndpoint,
                input.OAuthTokenEndpoint,
                input.OAuthScopes,
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
                _credentialStore.DeleteCredentialsForRemovedHeaders(existing, authSettings.Headers);
                if (!IsOAuthAuthType(existing.AuthType) || !IsOAuthAuthType(authType))
                {
                    _credentialStore.DeleteOAuthCredentials(existing);
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
            await _oauthService.RunInitialLoginIfNeededAsync(savedServer, _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"), _showError);
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
            if (_comfyUiTool.IsServerName(serverName))
            {
                return;
            }

            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            server.AuthType = NormalizeAuthType(server.AuthType, server.Headers);
            var existingHeader = server.Headers.FirstOrDefault();
            AgentMcpDialogInput? input = await _dialogService.ShowEditAsync(new AgentMcpDialogInput
            {
                Name = server.Name,
                Endpoint = server.Endpoint,
                AuthType = server.AuthType,
                HeaderName = existingHeader.Key ?? string.Empty,
                ApiKey = string.IsNullOrWhiteSpace(existingHeader.Key)
                    ? string.Empty
                    : _credentialStore.GetHeaderSecret(server, existingHeader.Key, existingHeader.Value),
                OAuthAccessToken = _credentialStore.GetOAuthAccessToken(server),
                OAuthClientId = server.OAuthClientId,
                OAuthClientSecret = _credentialStore.GetOAuthClientSecret(server),
                OAuthAuthorizationEndpoint = server.OAuthAuthorizationEndpoint,
                OAuthTokenEndpoint = server.OAuthTokenEndpoint,
                OAuthScopes = server.OAuthScopes
            });
            if (input == null)
            {
                return;
            }

            string name = input.Name.Trim();
            string endpoint = input.Endpoint.Trim();
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

            string authType = input.AuthType;
            if (!TryBuildAuthSettings(
                authType,
                input.HeaderName,
                input.ApiKey,
                input.OAuthAccessToken,
                input.OAuthClientId,
                input.OAuthClientSecret,
                input.OAuthAuthorizationEndpoint,
                input.OAuthTokenEndpoint,
                input.OAuthScopes,
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
                _credentialStore.DeleteAllCredentials(duplicate);
                _servers.Remove(duplicate);
                _selectedServerIds.Remove(duplicate.Id);
                _sessions.Remove(duplicate.Id);
                _serverStatus.Remove(duplicate.Id);
            }

            server.Name = name;
            server.Endpoint = endpoint;
            _credentialStore.DeleteCredentialsForRemovedHeaders(server, authSettings.Headers);
            if (!IsOAuthAuthType(server.AuthType) || !IsOAuthAuthType(authType))
            {
                _credentialStore.DeleteOAuthCredentials(server);
            }

            ApplyAuthSettings(server, authSettings, deleteEmptySecrets: true);
            _sessions.Remove(server.Id);
            _serverStatus.Remove(server.Id);
            await SaveAsync();
            await _oauthService.RunInitialLoginIfNeededAsync(server, _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"), _showError);
        }

        public async Task ConfigureBuiltInMcpAsync(string serverName)
        {
            if (_browserUseTool.IsServerName(serverName))
            {
                var browserSettings = _settingsService.CurrentSettings;
                var browserInput = await _dialogService.ShowBrowserUseSettingsAsync(new AgentMcpBrowserUseSettingsInput
                {
                    AllowInteraction = browserSettings.BrowserUseAllowInteraction,
                    CaptureEnabled = browserSettings.BrowserUseCaptureEnabled,
                    ComputerUseEnabled = browserSettings.BrowserUseComputerUseEnabled
                });
                if (browserInput == null)
                {
                    return;
                }

                browserSettings.BrowserUseAllowInteraction = browserInput.AllowInteraction;
                browserSettings.BrowserUseCaptureEnabled = browserInput.CaptureEnabled;
                browserSettings.BrowserUseComputerUseEnabled = browserInput.ComputerUseEnabled;
                await _settingsService.SaveSettingsAsync(browserSettings);
                RebuildAliases();
                UpdateUI();
                return;
            }

            if (!_comfyUiTool.IsServerName(serverName))
            {
                return;
            }

            var settings = _settingsService.CurrentSettings;
            string initialWorkflowDirectory = string.IsNullOrWhiteSpace(settings.ComfyUiWorkflowDirectory)
                ? EditorSettings.GetDefaultComfyUiWorkflowDirectory()
                : settings.ComfyUiWorkflowDirectory;
            var input = await _dialogService.ShowComfyUiSettingsAsync(new AgentMcpComfyUiSettingsInput
            {
                LaunchPath = settings.ComfyUiLaunchPath,
                WorkflowDirectory = initialWorkflowDirectory
            });
            if (input == null)
            {
                return;
            }

            if (!TryNormalizeOptionalExistingFilePath(input.LaunchPath, out string launchPath, out string launchError))
            {
                _showError(
                    _getString("AgentMcpComfyUiSettingsTitle", "ComfyUI 설정"),
                    launchError);
                return;
            }

            if (!TryNormalizeDirectoryPath(input.WorkflowDirectory, out string workflowDirectory, out string workflowError))
            {
                _showError(
                    _getString("AgentMcpComfyUiSettingsTitle", "ComfyUI 설정"),
                    workflowError);
                return;
            }

            settings.ComfyUiLaunchPath = launchPath;
            settings.ComfyUiWorkflowDirectory = workflowDirectory;
            await _settingsService.SaveSettingsAsync(settings);
            RebuildAliases();
            UpdateUI();
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
                        _credentialStore.DeleteCredentialsForRemovedHeaders(existing, headers);
                        existing.Endpoint = endpoint;
                        existing.Headers = _credentialStore.StoreHeaderSecrets(existing, headers);
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
                        server.Headers = _credentialStore.StoreHeaderSecrets(server, NormalizeHeaders(item.Headers));
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
            if (_browserUseTool.IsServerName(serverName))
            {
                if (!_selectedServerIds.Add(_browserUseTool.ServerId))
                {
                    _selectedServerIds.Remove(_browserUseTool.ServerId);
                }

                RebuildAliases();
                UpdateUI();
                return;
            }

            if (_comfyUiTool.IsServerName(serverName))
            {
                if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
                {
                    _selectedServerIds.Remove(_comfyUiTool.ServerId);
                    _comfyUiStatus = string.Empty;
                    RebuildAliases();
                    UpdateUI();
                    return;
                }

                _selectedServerIds.Add(_comfyUiTool.ServerId);
                RebuildAliases();
                UpdateUI();
                await EnsureBuiltInComfyUiReadyAsync(CancellationToken.None);
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
            if (_comfyUiTool.IsServerName(serverName) || _browserUseTool.IsServerName(serverName))
            {
                return;
            }

            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            _credentialStore.DeleteAllCredentials(server);
            _servers.Remove(server);
            _selectedServerIds.Remove(server.Id);
            _sessions.Remove(server.Id);
            _serverStatus.Remove(server.Id);
            await SaveAsync();
        }

        public void RemoveSelectedMcp(string serverName)
        {
            if (_browserUseTool.IsServerName(serverName))
            {
                _selectedServerIds.Remove(_browserUseTool.ServerId);
                RebuildAliases();
                UpdateUI();
                return;
            }

            if (_comfyUiTool.IsServerName(serverName))
            {
                _selectedServerIds.Remove(_comfyUiTool.ServerId);
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
            if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
            {
                labels.Add(_comfyUiTool.ServerName);
            }

            if (_selectedServerIds.Contains(_browserUseTool.ServerId))
            {
                labels.Add(_browserUseTool.ServerName);
            }

            labels.AddRange(GetSelectedServers().Select(server => server.Name));
            return string.Join(", ", labels);
        }

        public bool HasSelectedMcpServers()
        {
            return _selectedServerIds.Contains(_comfyUiTool.ServerId) ||
                _selectedServerIds.Contains(_browserUseTool.ServerId) ||
                GetSelectedServers().Count > 0;
        }

        public async Task EnsureActiveToolsAsync(CancellationToken cancellationToken)
        {
            RebuildAliases();
            if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
            {
                await EnsureBuiltInComfyUiReadyAsync(cancellationToken);
            }

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
            bool hasComfyUi = _selectedServerIds.Contains(_comfyUiTool.ServerId);
            bool hasBrowserUse = _selectedServerIds.Contains(_browserUseTool.ServerId);
            if (selectedServers.Count == 0 && !hasComfyUi && !hasBrowserUse)
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
                AppendMcpAliasSection(builder, _comfyUiTool.ServerName, _comfyUiTool.ServerId);
                _comfyUiTool.AppendWorkflowContext(builder);
            }

            if (hasBrowserUse)
            {
                AppendMcpAliasSection(builder, _browserUseTool.ServerName, _browserUseTool.ServerId);
                builder.AppendLine("Browser Use controls the installed Windows default browser through OS-level keyboard and mouse input.");
                if (_settingsService.CurrentSettings.BrowserUseCaptureEnabled)
                {
                    builder.AppendLine("For visual actions, always follow this loop: mcp_browser_use_capture -> read_image using the returned image_path -> mcp_browser_use_click with coordinates from that image -> capture again to verify. Use type_text only after visually clicking the intended input field.");
                }
                else
                {
                    builder.AppendLine("Browser image capture is disabled in plugin settings. Only use window coordinate clicks when coordinates are explicitly known.");
                }

                builder.AppendLine("Use read_page after navigation when selectable page text is needed.");
                if (_settingsService.CurrentSettings.BrowserUseComputerUseEnabled)
                {
                    builder.AppendLine("Computer Use is enabled. To control another app: list_windows or open_app -> focus_window when needed -> capture -> read_image -> click/type/key -> capture again. Never guess a window id or visual coordinate.");
                }
                builder.AppendLine();
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
            lock (_toolAliases)
            {
                return _toolAliases.TryGetValue(aliasName, out alias!);
            }
        }

        public IReadOnlyList<AgentMcpToolAlias> GetActiveToolAliases()
        {
            lock (_toolAliases)
            {
                return _toolAliases.Values.ToList();
            }
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
                return $"MCP tool failed: {_credentialStore.RedactServerSecrets(server, error)}";
            }

            if (!TryGetResult(response.RootElement, out var result))
            {
                return "MCP tool returned no result.";
            }

            return _credentialStore.RedactServerSecrets(server, FormatToolCallResult(alias, result));
        }

        private async Task<string> ExecuteBuiltInToolAsync(
            AgentMcpToolAlias alias,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (_comfyUiTool.CanHandleAlias(alias))
            {
                return await _comfyUiTool.ExecuteAsync(alias, arguments, cancellationToken);
            }

            if (_browserUseTool.CanHandleAlias(alias))
            {
                return await _browserUseTool.ExecuteAsync(alias, arguments, cancellationToken);
            }

            return $"MCP tool failed: unknown built-in MCP tool alias: {alias.Alias}";
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
                    string headerValue = _credentialStore.GetHeaderSecret(server, header.Key, header.Value);
                    if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(headerValue))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, headerValue);
                    }
                }
            }
            else if (IsOAuthAuthType(server.AuthType))
            {
                string accessToken = await _oauthService.EnsureAccessTokenAsync(server, cancellationToken);
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
                throw new InvalidOperationException(_credentialStore.RedactServerSecrets(server, $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}"));
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
            lock (_toolAliases)
            {
                _toolAliases.Clear();
            var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
            {
                foreach (AgentMcpToolAlias alias in _comfyUiTool.CreateAliases())
                {
                    usedAliases.Add(alias.Alias);
                    _toolAliases[alias.Alias] = alias;
                }
            }

            if (_selectedServerIds.Contains(_browserUseTool.ServerId))
            {
                foreach (AgentMcpToolAlias alias in _browserUseTool.CreateAliases())
                {
                    usedAliases.Add(alias.Alias);
                    _toolAliases[alias.Alias] = alias;
                }
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
        }

        private void UpdateUI()
        {
            var items = new List<AgentMcpItem>
            {
                _comfyUiTool.CreateMenuItem(_selectedServerIds.Contains(_comfyUiTool.ServerId), _getString, _comfyUiStatus),
                _browserUseTool.CreateMenuItem(_selectedServerIds.Contains(_browserUseTool.ServerId), _getString)
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
            if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
            {
                selectedNames.Add(_comfyUiTool.ServerName);
            }

            if (_selectedServerIds.Contains(_browserUseTool.ServerId))
            {
                selectedNames.Add(_browserUseTool.ServerName);
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

        private async Task EnsureBuiltInComfyUiReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await _comfyUiTool.IsServerRunningAsync(cancellationToken))
                {
                    _comfyUiStatus = _getString("AgentMcpComfyUiStartStatusRunning", "실행 중");
                    UpdateUI();
                    return;
                }

                if (!_comfyUiTool.TryStartConfiguredComfyUi(out ComfyUiLaunchFailure failure, out string detail))
                {
                    _comfyUiStatus = failure switch
                    {
                        ComfyUiLaunchFailure.MissingPath => _getString("AgentMcpComfyUiStartStatusLaunchPathMissing", "실행 파일 경로 필요"),
                        ComfyUiLaunchFailure.FileNotFound => string.Format(
                            _getString("AgentMcpComfyUiStartStatusLaunchPathNotFound", "실행 파일 없음: {0}"),
                            detail),
                        _ => string.Format(
                            _getString("AgentMcpComfyUiStartStatusFailedFormat", "실행 실패: {0}"),
                            detail)
                    };
                    UpdateUI();
                    return;
                }

                _comfyUiStatus = _getString("AgentMcpComfyUiStartStatusStarting", "실행 시작 중");
                UpdateUI();
                bool becameReady = await _comfyUiTool.WaitForServerRunningAsync(TimeSpan.FromSeconds(12), cancellationToken);
                _comfyUiStatus = becameReady
                    ? _getString("AgentMcpComfyUiStartStatusRunning", "실행 중")
                    : _getString("AgentMcpComfyUiStartStatusStarted", "실행 시작됨");
                UpdateUI();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _comfyUiStatus = string.Format(
                    _getString("AgentMcpComfyUiStartStatusFailedFormat", "실행 실패: {0}"),
                    ex.Message);
                UpdateUI();
            }
        }

        private bool TryNormalizeOptionalExistingFilePath(string path, out string normalizedPath, out string error)
        {
            normalizedPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            try
            {
                normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
                if (File.Exists(normalizedPath))
                {
                    return true;
                }

                error = string.Format(
                    _getString("AgentMcpComfyUiStartStatusLaunchPathNotFound", "실행 파일 없음: {0}"),
                    normalizedPath);
                return false;
            }
            catch (Exception ex)
            {
                error = string.Format(
                    _getString("AgentMcpComfyUiStartStatusFailedFormat", "실행 실패: {0}"),
                    ex.Message);
                return false;
            }
        }

        private bool TryNormalizeDirectoryPath(string path, out string normalizedPath, out string error)
        {
            normalizedPath = string.Empty;
            error = string.Empty;
            try
            {
                string requestedPath = string.IsNullOrWhiteSpace(path)
                    ? EditorSettings.GetDefaultComfyUiWorkflowDirectory()
                    : path.Trim().Trim('"');
                normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath));
                Directory.CreateDirectory(normalizedPath);
                return true;
            }
            catch (Exception ex)
            {
                error = string.Format(
                    _getString("AgentMcpComfyUiWorkflowDirectoryErrorFormat", "워크플로우 폴더를 사용할 수 없습니다: {0}"),
                    ex.Message);
                return false;
            }
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
                ? _credentialStore.StoreHeaderSecrets(server, settings.Headers, deleteEmptySecrets)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!settings.AuthType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase))
            {
                _credentialStore.DeleteCredentialsForRemovedHeaders(server, server.Headers);
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
                server.OAuthAccessToken = _credentialStore.StoreOAuthSecret(server, "access_token", settings.OAuthAccessToken, deleteEmptySecrets);
                server.OAuthRefreshToken = string.Empty;
                server.OAuthClientSecret = string.Empty;
                server.OAuthAccessTokenExpiresAt = DateTimeOffset.MaxValue;
            }
            else if (settings.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase))
            {
                server.OAuthAccessToken = _credentialStore.StoreOAuthSecret(server, "access_token", settings.OAuthAccessToken, deleteEmptySecrets);
                server.OAuthRefreshToken = _credentialStore.StoreOAuthSecret(server, "refresh_token", settings.OAuthRefreshToken, deleteEmptySecrets);
                server.OAuthClientSecret = _credentialStore.StoreOAuthSecret(server, "client_secret", settings.OAuthClientSecret, deleteEmptySecrets);
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

    }
}
