using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpServerEditor
    {
        private readonly AgentMcpRegistry _registry;
        private readonly AgentMcpConnectionService _connections;
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly AgentMcpConfigurationService _configuration;
        private readonly AgentMcpOAuthService _oauthService;
        private readonly AgentMcpDialogService _dialogService;
        private readonly ISet<string> _selectedServerIds;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Func<Task> _saveAsync;

        public AgentMcpServerEditor(
            AgentMcpRegistry registry,
            AgentMcpConnectionService connections,
            AgentMcpCredentialStore credentialStore,
            AgentMcpConfigurationService configuration,
            AgentMcpOAuthService oauthService,
            AgentMcpDialogService dialogService,
            ISet<string> selectedServerIds,
            Action<object> initializePickerWindow,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Func<Task> saveAsync)
        {
            _registry = registry;
            _connections = connections;
            _credentialStore = credentialStore;
            _configuration = configuration;
            _oauthService = oauthService;
            _dialogService = dialogService;
            _selectedServerIds = selectedServerIds;
            _initializePickerWindow = initializePickerWindow;
            _showError = showError;
            _getString = getString;
            _saveAsync = saveAsync;
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
            bool allowAutomaticOAuth =
                !AgentMcpTransportTypes.IsStdio(connection.Transport) &&
                authType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(input.OAuthClientId) ||
                 string.IsNullOrWhiteSpace(input.OAuthAuthorizationEndpoint) ||
                 string.IsNullOrWhiteSpace(input.OAuthTokenEndpoint));
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
                allowAutomaticOAuth,
                out var authSettings,
                out string authError))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    authError);
                return;
            }

            AgentMcpServer? savedServer;
            var existing = _registry.Servers.FirstOrDefault(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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
                _connections.Reset(existing.Id);
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
                _registry.Servers.Add(server);
                savedServer = server;
            }

            await _saveAsync();
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
                string json = JsonSerializer.Serialize(_registry.Servers, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(file.Path, json);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentMcpExportErrorTitle", "MCP 내보내기 오류"),
                    string.Format(_getString("AgentMcpExportErrorMessage", "MCP 목록을 내보내는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        public async Task EditMcpAsync(AgentMcpServer server)
        {
            server.AuthType = NormalizeAuthType(server.AuthType, server.Headers);
            var existingHeader = server.Headers.FirstOrDefault();
            AgentMcpDialogInput? input = await _dialogService.ShowEditAsync(new AgentMcpDialogInput
            {
                Name = server.Name,
                Transport = server.Transport,
                Stateless = server.Stateless,
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
            bool allowAutomaticOAuth =
                !AgentMcpTransportTypes.IsStdio(connection.Transport) &&
                authType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(input.OAuthClientId) ||
                 string.IsNullOrWhiteSpace(input.OAuthAuthorizationEndpoint) ||
                 string.IsNullOrWhiteSpace(input.OAuthTokenEndpoint));
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
                allowAutomaticOAuth,
                out var authSettings,
                out string authError))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    authError);
                return;
            }

            var duplicate = _registry.Servers.FirstOrDefault(item =>
                !item.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                _credentialStore.DeleteAllCredentials(duplicate);
                _registry.Servers.Remove(duplicate);
                _selectedServerIds.Remove(duplicate.Id);
                _connections.Reset(duplicate.Id);
            }

            string previousAuthType = server.AuthType;
            string previousOAuthClientId = server.OAuthClientId;
            string previousOAuthAuthorizationEndpoint = server.OAuthAuthorizationEndpoint;
            string previousOAuthTokenEndpoint = server.OAuthTokenEndpoint;
            string previousOAuthRefreshToken = _credentialStore.GetOAuthSecret(
                server,
                "refresh_token",
                server.OAuthRefreshToken);
            DateTimeOffset previousOAuthAccessTokenExpiresAt = server.OAuthAccessTokenExpiresAt;

            server.Name = name;
            _credentialStore.DeleteCredentialsForRemovedHeaders(server, authSettings.Headers);
            _credentialStore.DeleteCredentialsForRemovedEnvironment(server, connection.Environment);
            if (!IsOAuthAuthType(server.AuthType) || !IsOAuthAuthType(authType))
            {
                _credentialStore.DeleteOAuthCredentials(server);
            }

            _configuration.ApplyConnectionSettings(server, connection);
            _configuration.ApplyAuthSettings(server, authSettings, deleteEmptySecrets: true);
            bool preserveOAuthRefreshToken =
                previousAuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) &&
                authType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) &&
                previousOAuthClientId.Equals(server.OAuthClientId, StringComparison.Ordinal) &&
                previousOAuthAuthorizationEndpoint.Equals(server.OAuthAuthorizationEndpoint, StringComparison.Ordinal) &&
                previousOAuthTokenEndpoint.Equals(server.OAuthTokenEndpoint, StringComparison.Ordinal);
            if (preserveOAuthRefreshToken)
            {
                server.OAuthRefreshToken = _credentialStore.StoreOAuthSecret(
                    server,
                    "refresh_token",
                    previousOAuthRefreshToken,
                    deleteEmptySecret: false);
                server.OAuthAccessTokenExpiresAt = previousOAuthAccessTokenExpiresAt;
            }

            _connections.Reset(server.Id);
            await _saveAsync();
            await _oauthService.RunInitialLoginIfNeededAsync(server, _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"), _showError);
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
                    item.Stateless = !AgentMcpTransportTypes.IsStdio(item.Transport) && item.Stateless;
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

                    var existing = _registry.FindServer(name);
                    if (existing != null)
                    {
                        var headers = AgentMcpConfigurationService.NormalizeHeaders(item.Headers);
                        _credentialStore.DeleteCredentialsForRemovedHeaders(existing, headers);
                        _credentialStore.DeleteCredentialsForRemovedEnvironment(existing, item.Environment);
                        existing.Transport = item.Transport;
                        existing.Stateless = item.Stateless;
                        existing.Endpoint = item.Endpoint;
                        existing.Command = item.Command;
                        existing.Arguments = item.Arguments;
                        existing.WorkingDirectory = item.WorkingDirectory;
                        existing.TargetDirectory = item.TargetDirectory;
                        existing.Environment = _credentialStore.StoreEnvironmentSecrets(existing, item.Environment);
                        existing.Headers = _credentialStore.StoreHeaderSecrets(existing, headers);
                        _connections.Reset(existing.Id);
                    }
                    else
                    {
                        var server = new AgentMcpServer
                        {
                            Name = name,
                            Transport = item.Transport,
                            Stateless = item.Stateless,
                            Endpoint = item.Endpoint,
                            Command = item.Command,
                            Arguments = item.Arguments,
                            WorkingDirectory = item.WorkingDirectory,
                            TargetDirectory = item.TargetDirectory
                        };
                        server.Environment = _credentialStore.StoreEnvironmentSecrets(server, item.Environment);
                        server.Headers = _credentialStore.StoreHeaderSecrets(server, AgentMcpConfigurationService.NormalizeHeaders(item.Headers));
                        _registry.Servers.Add(server);
                    }
                }

                await _saveAsync();
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentMcpImportErrorTitle", "MCP 가져오기 오류"),
                    string.Format(_getString("AgentMcpImportErrorMessage", "MCP 목록을 가져오는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }
    }
}
