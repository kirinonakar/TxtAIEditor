using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal sealed class AgentMcpController
    {
        private readonly AgentPane _agentPane;
        private readonly Action<object> _initializePickerWindow;
        private readonly ISettingsService _settingsService;
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly AgentMcpConfigurationService _configuration;
        private readonly AgentMcpOAuthService _oauthService;
        private readonly AgentMcpDialogService _dialogService;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Action _contextChanged;
        private readonly Func<string> _workspaceRootProvider;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly AgentMcpComfyUiTool _comfyUiTool;
        private readonly AgentMcpBrowserUseTool _browserUseTool;
        private readonly AgentMcpRuntime _runtime;
        private readonly string _mcpFilePath;
        private readonly List<AgentMcpServer> _servers = new();
        private readonly HashSet<string> _selectedServerIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ServerStartOperation> _serverStartOperations = new(StringComparer.OrdinalIgnoreCase);
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
            Action? afterDialog,
            Action<TxtAIEditor.Core.Services.LLM.LlmMessageAttachment>? addImageAttachment = null)
        {
            _agentPane = agentPane;
            _initializePickerWindow = initializePickerWindow;
            _settingsService = settingsService;
            _showError = showError;
            _getString = getString;
            _credentialStore = new AgentMcpCredentialStore(credentialService);
            _configuration = new AgentMcpConfigurationService(_credentialStore, _getString);
            _oauthService = new AgentMcpOAuthService(_credentialStore, _getString, SaveAsync);
            _runtime = new AgentMcpRuntime(_credentialStore, _oauthService, workspaceRootProvider, _getString);
            _contextChanged = contextChanged;
            _workspaceRootProvider = workspaceRootProvider;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
            _dialogService = new AgentMcpDialogService(_agentPane, _initializePickerWindow, _getString, _beforeDialog, _afterDialog);
            _comfyUiTool = new AgentMcpComfyUiTool(workspaceRootProvider, () => _settingsService.CurrentSettings, fileModifiedAsync);
            _browserUseTool = new AgentMcpBrowserUseTool(() => _settingsService.CurrentSettings, addImageAttachment);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _mcpFilePath = Path.Combine(settingsDir, "agent-mcp-servers.json");
        }

        public async Task LoadAsync()
        {
            bool migratedPlaintextHeaders = false;
            bool migratedPlaintextOAuth = false;
            bool migratedPlaintextEnvironment = false;
            bool migratedInlineStdioCommands = false;
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
                            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && AgentMcpConfigurationService.HasValidConnectionSettings(s))
                            .Select(s =>
                            {
                                if (string.IsNullOrWhiteSpace(s.Id))
                                {
                                    s.Id = Guid.NewGuid().ToString("N");
                                }

                                s.Name = s.Name.Trim();
                                s.Transport = AgentMcpTransportTypes.Normalize(s.Transport, s.Command);
                                s.Endpoint = s.Endpoint?.Trim() ?? string.Empty;
                                s.Command = s.Command?.Trim() ?? string.Empty;
                                s.Arguments = AgentMcpConfigurationService.NormalizeArguments(s.Arguments);
                                if (AgentMcpTransportTypes.IsStdio(s.Transport))
                                {
                                    migratedInlineStdioCommands |= AgentMcpConfigurationService.NormalizeStdioCommand(s);
                                }
                                s.WorkingDirectory = s.WorkingDirectory?.Trim() ?? string.Empty;
                                s.TargetDirectory = s.TargetDirectory?.Trim() ?? string.Empty;
                                s.Environment = AgentMcpConfigurationService.NormalizeEnvironment(s.Environment);
                                s.Headers = AgentMcpConfigurationService.NormalizeHeaders(s.Headers);
                                s.AuthType = NormalizeAuthType(s.AuthType, s.Headers);
                                migratedPlaintextHeaders |= s.Headers.Values.Any(value => !string.IsNullOrWhiteSpace(value));
                                migratedPlaintextOAuth |= !string.IsNullOrWhiteSpace(s.OAuthAccessToken) ||
                                    !string.IsNullOrWhiteSpace(s.OAuthRefreshToken) ||
                                    !string.IsNullOrWhiteSpace(s.OAuthClientSecret);
                                migratedPlaintextEnvironment |= s.Environment.Values.Any(value => !string.IsNullOrEmpty(value));
                                _credentialStore.MoveInlineHeaderValuesToCredential(s);
                                _credentialStore.MoveInlineOAuthSecretsToCredential(s);
                                _credentialStore.MoveInlineEnvironmentValuesToCredential(s);
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
            if (migratedPlaintextHeaders ||
                migratedPlaintextOAuth ||
                migratedPlaintextEnvironment ||
                migratedInlineStdioCommands)
            {
                await SaveAsync();
                return;
            }

            RebuildAliases();
            UpdateUI();
        }

        public void Close()
        {
            foreach (var operation in _serverStartOperations.Values)
            {
                operation.Cancel();
            }
            _serverStartOperations.Clear();
            _runtime.Dispose();
        }

        public async Task AddMcpAsync()
        {
            AgentMcpDialogInput? input = await _dialogService.ShowAddAsync();
            if (input == null)
            {
                return;
            }

            string name = input.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    _getString("AgentMcpNameRequired", "MCP 이름을 입력해주세요."));
                return;
            }

            if (!_configuration.TryParseConnectionInput(input, out var connection, out string connectionError))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    connectionError);
                return;
            }

            string authType = AgentMcpTransportTypes.IsStdio(connection.Transport) ? AuthTypeNone : input.AuthType;
            if (!_configuration.TryBuildAuthSettings(
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
                _credentialStore.DeleteCredentialsForRemovedEnvironment(existing, connection.Environment);
                if (!IsOAuthAuthType(existing.AuthType) || !IsOAuthAuthType(authType))
                {
                    _credentialStore.DeleteOAuthCredentials(existing);
                }

                _configuration.ApplyConnectionSettings(existing, connection);
                _configuration.ApplyAuthSettings(existing, authSettings, deleteEmptySecrets: true);
                _runtime.RemoveSession(existing.Id);
                _serverStatus.Remove(existing.Id);
                savedServer = existing;
            }
            else
            {
                var server = new AgentMcpServer
                {
                    Name = name
                };
                _configuration.ApplyConnectionSettings(server, connection);
                _configuration.ApplyAuthSettings(server, authSettings, deleteEmptySecrets: true);
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
                Transport = server.Transport,
                Endpoint = server.Endpoint,
                Command = server.Command,
                ArgumentsJson = JsonSerializer.Serialize(server.Arguments),
                WorkingDirectory = server.WorkingDirectory,
                TargetDirectory = server.TargetDirectory,
                EnvironmentJson = JsonSerializer.Serialize(server.Environment.ToDictionary(
                    item => item.Key,
                    item => _credentialStore.GetEnvironmentSecret(server, item.Key, item.Value),
                    StringComparer.OrdinalIgnoreCase)),
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
            if (string.IsNullOrWhiteSpace(name))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    _getString("AgentMcpNameRequired", "MCP 이름을 입력해주세요."));
                return;
            }

            if (!_configuration.TryParseConnectionInput(input, out var connection, out string connectionError))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    connectionError);
                return;
            }

            string authType = AgentMcpTransportTypes.IsStdio(connection.Transport) ? AuthTypeNone : input.AuthType;
            if (!_configuration.TryBuildAuthSettings(
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
                _runtime.RemoveSession(duplicate.Id);
                _serverStatus.Remove(duplicate.Id);
            }

            server.Name = name;
            _credentialStore.DeleteCredentialsForRemovedHeaders(server, authSettings.Headers);
            _credentialStore.DeleteCredentialsForRemovedEnvironment(server, connection.Environment);
            if (!IsOAuthAuthType(server.AuthType) || !IsOAuthAuthType(authType))
            {
                _credentialStore.DeleteOAuthCredentials(server);
            }

            _configuration.ApplyConnectionSettings(server, connection);
            _configuration.ApplyAuthSettings(server, authSettings, deleteEmptySecrets: true);
            _runtime.RemoveSession(server.Id);
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

            if (!_configuration.TryNormalizeOptionalExistingFilePath(input.LaunchPath, out string launchPath, out string launchError))
            {
                _showError(
                    _getString("AgentMcpComfyUiSettingsTitle", "ComfyUI 설정"),
                    launchError);
                return;
            }

            if (!_configuration.TryNormalizeDirectoryPath(input.WorkflowDirectory, out string workflowDirectory, out string workflowError))
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
                var imported = AgentMcpConfigurationService.DeserializeImportedServers(json);
                if (imported.Count == 0)
                {
                    throw new InvalidDataException(_getString("AgentMcpImportInvalidFile", "가져올 수 있는 MCP JSON이 아닙니다."));
                }

                foreach (var item in imported)
                {
                    string name = item.Name?.Trim() ?? string.Empty;
                    item.Transport = AgentMcpTransportTypes.Normalize(item.Transport, item.Command);
                    item.Endpoint = item.Endpoint?.Trim() ?? string.Empty;
                    item.Command = item.Command?.Trim() ?? string.Empty;
                    item.Arguments = AgentMcpConfigurationService.NormalizeArguments(item.Arguments);
                    if (AgentMcpTransportTypes.IsStdio(item.Transport))
                    {
                        AgentMcpConfigurationService.NormalizeStdioCommand(item);
                    }
                    item.WorkingDirectory = item.WorkingDirectory?.Trim() ?? string.Empty;
                    item.TargetDirectory = item.TargetDirectory?.Trim() ?? string.Empty;
                    item.Environment = AgentMcpConfigurationService.NormalizeEnvironment(item.Environment);
                    if (string.IsNullOrWhiteSpace(name) || !AgentMcpConfigurationService.HasValidConnectionSettings(item))
                    {
                        continue;
                    }

                    var existing = FindServer(name);
                    if (existing != null)
                    {
                        var headers = AgentMcpConfigurationService.NormalizeHeaders(item.Headers);
                        _credentialStore.DeleteCredentialsForRemovedHeaders(existing, headers);
                        _credentialStore.DeleteCredentialsForRemovedEnvironment(existing, item.Environment);
                        existing.Transport = item.Transport;
                        existing.Endpoint = item.Endpoint;
                        existing.Command = item.Command;
                        existing.Arguments = item.Arguments;
                        existing.WorkingDirectory = item.WorkingDirectory;
                        existing.TargetDirectory = item.TargetDirectory;
                        existing.Environment = _credentialStore.StoreEnvironmentSecrets(existing, item.Environment);
                        existing.Headers = _credentialStore.StoreHeaderSecrets(existing, headers);
                        _runtime.RemoveSession(existing.Id);
                        _serverStatus.Remove(existing.Id);
                    }
                    else
                    {
                        var server = new AgentMcpServer
                        {
                            Name = name,
                            Transport = item.Transport,
                            Endpoint = item.Endpoint,
                            Command = item.Command,
                            Arguments = item.Arguments,
                            WorkingDirectory = item.WorkingDirectory,
                            TargetDirectory = item.TargetDirectory
                        };
                        server.Environment = _credentialStore.StoreEnvironmentSecrets(server, item.Environment);
                        server.Headers = _credentialStore.StoreHeaderSecrets(server, AgentMcpConfigurationService.NormalizeHeaders(item.Headers));
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
                StopServer(server.Id);
                RebuildAliases();
                UpdateUI();
                return;
            }

            UpdateUI();
            await StartServerAsync(server, CancellationToken.None);
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
            StopServer(server.Id);
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
            StopServer(server.Id);
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
                if (_runtime.GetTools(server.Id).Count == 0)
                {
                    using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readinessCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await StartServerAsync(server, readinessCancellation.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Keep the shared MCP start running, but do not block the Agent prompt indefinitely.
                    }
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
            builder.AppendLine("Never emit a tool_call with an empty or invented name. If a tool action is intended, copy one exact enabled alias and provide all required arguments.");
            builder.AppendLine();

            if (hasComfyUi)
            {
                AppendMcpAliasSection(builder, _comfyUiTool.ServerName, _comfyUiTool.ServerId);
                _comfyUiTool.AppendWorkflowContext(builder);
            }

            if (hasBrowserUse)
            {
                AppendMcpAliasSection(builder, _browserUseTool.ServerName, _browserUseTool.ServerId);
                builder.AppendLine("Browser Use & Computer Use controls the installed Windows default browser and other Windows applications through OS-level keyboard and mouse input.");
                if (_settingsService.CurrentSettings.BrowserUseCaptureEnabled)
                {
                    builder.AppendLine("Accessibility snapshots provide stable refs, and interaction tools return a fresh snapshot.");
                    builder.AppendLine("mcp_browser_use_capture is available whenever you determine that screenshot context would help with the task.");
                }
                else
                {
                    builder.AppendLine("Browser image capture is disabled in plugin settings. Use accessibility snapshots and stable refs for interactions.");
                }

                builder.AppendLine("Use read_page after navigation when selectable page text is needed.");
                if (_settingsService.CurrentSettings.BrowserUseComputerUseEnabled)
                {
                    builder.AppendLine("Computer Use is enabled. open_app returns an initial accessibility tree with stable refs. After list_windows or open_app, use focus_window when needed and choose between accessibility refs and capture based on the current task. Never guess a window id, ref, or visual coordinate.");
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

            using JsonDocument response = await _runtime.ExecuteToolAsync(
                server,
                alias.ToolName,
                arguments,
                cancellationToken);

            if (AgentMcpRuntime.TryGetRpcError(response.RootElement, out string error))
            {
                return $"MCP tool failed: {_credentialStore.RedactServerSecrets(server, error)}";
            }

            if (!AgentMcpRuntime.TryGetResult(response.RootElement, out var result))
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

        private async Task StartServerAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            if (!_serverStartOperations.TryGetValue(server.Id, out var operation))
            {
                operation = new ServerStartOperation();
                _serverStartOperations[server.Id] = operation;
                operation.Task = RunServerStartAsync(server, operation);
            }

            await operation.Task.WaitAsync(cancellationToken);
        }

        private async Task RunServerStartAsync(AgentMcpServer server, ServerStartOperation operation)
        {
            try
            {
                await RefreshServerToolsAsync(server, operation.Token);
            }
            finally
            {
                if (_serverStartOperations.TryGetValue(server.Id, out var current) &&
                    ReferenceEquals(current, operation))
                {
                    _serverStartOperations.Remove(server.Id);
                }

                operation.Dispose();
            }
        }

        private async Task RefreshServerToolsAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            try
            {
                _serverStatus[server.Id] = _getString("AgentMcpStatusConnecting", "연결 중");
                UpdateUI();
                IReadOnlyList<AgentMcpTool> tools = await _runtime.RefreshToolsAsync(server, cancellationToken);

                if (_selectedServerIds.Contains(server.Id) && !cancellationToken.IsCancellationRequested)
                {
                    _serverStatus[server.Id] = string.Format(
                        _getString("AgentMcpStatusToolCount", "{0:N0}개 도구"),
                        tools.Count);
                }
                else
                {
                    _runtime.RemoveSession(server.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (_selectedServerIds.Contains(server.Id) && !cancellationToken.IsCancellationRequested)
                {
                    _serverStatus[server.Id] = string.Format(
                        _getString("AgentMcpStatusFailedFormat", "연결 실패: {0}"),
                        ex.Message);
                }
            }

            RebuildAliases();
            UpdateUI();
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
                IReadOnlyList<AgentMcpTool> tools = _runtime.GetTools(server.Id);
                if (tools.Count == 0)
                {
                    continue;
                }

                foreach (var tool in tools)
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

        private void CancelServerStart(string serverId)
        {
            if (_serverStartOperations.Remove(serverId, out var operation))
            {
                operation.Cancel();
            }
        }

        private void StopServer(string serverId)
        {
            CancelServerStart(serverId);
            _runtime.RemoveSession(serverId);
            _serverStatus.Remove(serverId);
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
                    bool isMemoryServer = AgentMcpRuntime.IsMemoryServer(server);
                    IEnumerable<string> displayArguments = isMemoryServer
                        ? server.Arguments
                        : server.Arguments.Append(server.TargetDirectory);
                    string connectionArguments = string.Join(" ", displayArguments
                        .Where(argument => !string.IsNullOrWhiteSpace(argument)));
                    string connectionDetail = AgentMcpTransportTypes.IsStdio(server.Transport)
                        ? $"stdio: {server.Command} {connectionArguments}".TrimEnd()
                        : server.Endpoint;
                    if (isMemoryServer && !string.IsNullOrWhiteSpace(server.TargetDirectory))
                    {
                        connectionDetail += $" - memory: {Path.Combine(server.TargetDirectory, "memory.jsonl")}";
                    }
                    string detail = _serverStatus.TryGetValue(server.Id, out string? status) && !string.IsNullOrWhiteSpace(status)
                        ? $"{connectionDetail} - {status}"
                        : connectionDetail;
                    if (!AgentMcpTransportTypes.IsStdio(server.Transport) && server.Headers.Count > 0)
                    {
                        detail += $" - headers: {string.Join(", ", server.Headers.Keys)}";
                    }

                    return new AgentMcpItem
                    {
                        Name = server.Name,
                        Endpoint = connectionDetail,
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

        private AgentMcpServer? FindServer(string name)
        {
            return _servers.FirstOrDefault(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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

        private sealed class ServerStartOperation : IDisposable
        {
            private readonly CancellationTokenSource _cancellation = new();

            public CancellationToken Token => _cancellation.Token;
            public Task Task { get; set; } = Task.CompletedTask;

            public void Cancel()
            {
                _cancellation.Cancel();
            }

            public void Dispose()
            {
                _cancellation.Dispose();
            }
        }

    }
}
