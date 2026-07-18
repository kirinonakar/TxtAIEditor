using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpConfigurationService
    {
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly Func<string, string, string> _getString;

        public AgentMcpConfigurationService(
            AgentMcpCredentialStore credentialStore,
            Func<string, string, string> getString)
        {
            _credentialStore = credentialStore;
            _getString = getString;
        }

        public bool TryNormalizeOptionalExistingFilePath(string path, out string normalizedPath, out string error)
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

        public bool TryNormalizeDirectoryPath(string path, out string normalizedPath, out string error)
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

        public static List<AgentMcpServer> DeserializeImportedServers(string json)
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

                string command = TryGetStringProperty(property.Value, "command");

                servers.Add(new AgentMcpServer
                {
                    Name = property.Name,
                    Transport = AgentMcpTransportTypes.Normalize(
                        TryGetStringProperty(property.Value, "transport"),
                        command),
                    Endpoint = endpoint,
                    Command = command,
                    Arguments = ReadStringArrayProperty(property.Value, "args"),
                    WorkingDirectory = FirstNonEmpty(
                        TryGetStringProperty(property.Value, "cwd"),
                        TryGetStringProperty(property.Value, "workingDirectory")),
                    TargetDirectory = TryGetStringProperty(property.Value, "targetDirectory"),
                    Environment = ReadStringDictionaryProperty(property.Value, "env"),
                    Headers = ReadHeadersProperty(property.Value)
                });
            }

            return servers;
        }

        private static Dictionary<string, string> ReadHeadersProperty(JsonElement element)
        {
            return ReadStringDictionaryProperty(element, "headers");
        }

        private static Dictionary<string, string> ReadStringDictionaryProperty(JsonElement element, string propertyName)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var objectElement) ||
                objectElement.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (var property in objectElement.EnumerateObject())
            {
                string key = property.Name.Trim();
                string value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values[key] = value;
                }
            }

            return values;
        }

        private static List<string> ReadStringArrayProperty(JsonElement element, string propertyName)
        {
            var values = new List<string>();
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var arrayElement) ||
                arrayElement.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (var item in arrayElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    values.Add(item.GetString() ?? string.Empty);
                }
            }

            return values;
        }

        public static Dictionary<string, string> NormalizeHeaders(Dictionary<string, string>? headers)
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

        public static Dictionary<string, string> NormalizeEnvironment(Dictionary<string, string>? environment)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (environment == null)
            {
                return normalized;
            }

            foreach (var variable in environment)
            {
                string key = variable.Key?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    normalized[key] = variable.Value ?? string.Empty;
                }
            }

            return normalized;
        }

        public static List<string> NormalizeArguments(IEnumerable<string>? arguments)
        {
            return arguments?.Where(argument => argument != null).ToList() ?? new List<string>();
        }

        public bool TryParseConnectionInput(
            AgentMcpDialogInput input,
            out AgentMcpConnectionSettings settings,
            out string error)
        {
            settings = new AgentMcpConnectionSettings
            {
                Transport = AgentMcpTransportTypes.Normalize(input.Transport, input.Command),
                Endpoint = input.Endpoint?.Trim() ?? string.Empty,
                Command = input.Command?.Trim() ?? string.Empty,
                WorkingDirectory = input.WorkingDirectory?.Trim() ?? string.Empty,
                TargetDirectory = input.TargetDirectory?.Trim() ?? string.Empty
            };
            error = string.Empty;

            if (!AgentMcpTransportTypes.IsStdio(settings.Transport))
            {
                if (!IsValidHttpEndpoint(settings.Endpoint))
                {
                    error = _getString("AgentMcpEndpointInvalid", "MCP 주소는 http 또는 https URL이어야 합니다.");
                    return false;
                }

                return true;
            }

            if (string.IsNullOrWhiteSpace(settings.Command))
            {
                error = _getString("AgentMcpCommandRequired", "stdio 실행 명령을 입력해주세요.");
                return false;
            }

            try
            {
                using JsonDocument argumentsDocument = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(input.ArgumentsJson) ? "[]" : input.ArgumentsJson);
                if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Array ||
                    argumentsDocument.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                {
                    throw new JsonException();
                }

                settings.Arguments = argumentsDocument.RootElement
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .ToList();
                NormalizeStdioCommandLine(settings.Command, settings.Arguments, out string command, out var arguments);
                settings.Command = command;
                settings.Arguments = arguments;
            }
            catch (JsonException)
            {
                error = _getString("AgentMcpArgumentsInvalid", "stdio 인수는 문자열로 구성된 JSON 배열이어야 합니다.");
                return false;
            }

            try
            {
                using JsonDocument environmentDocument = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(input.EnvironmentJson) ? "{}" : input.EnvironmentJson);
                if (environmentDocument.RootElement.ValueKind != JsonValueKind.Object ||
                    environmentDocument.RootElement.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.String))
                {
                    throw new JsonException();
                }

                settings.Environment = environmentDocument.RootElement
                    .EnumerateObject()
                    .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                    .ToDictionary(
                        item => item.Name,
                        item => item.Value.GetString() ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                error = _getString("AgentMcpEnvironmentInvalid", "환경 변수는 문자열 값으로 구성된 JSON 객체여야 합니다.");
                return false;
            }

            return true;
        }

        public static bool NormalizeStdioCommand(AgentMcpServer server)
        {
            string originalCommand = server.Command;
            int originalArgumentCount = server.Arguments.Count;
            NormalizeStdioCommandLine(server.Command, server.Arguments, out string command, out var arguments);
            server.Command = command;
            server.Arguments = arguments;
            return !string.Equals(originalCommand, command, StringComparison.Ordinal) ||
                originalArgumentCount != arguments.Count;
        }

        private static void NormalizeStdioCommandLine(
            string commandLine,
            IReadOnlyList<string> configuredArguments,
            out string command,
            out List<string> arguments)
        {
            commandLine = commandLine?.Trim() ?? string.Empty;
            string expandedPath = Environment.ExpandEnvironmentVariables(commandLine.Trim('"'));
            if (File.Exists(expandedPath))
            {
                command = commandLine.Trim('"');
                arguments = configuredArguments.ToList();
                return;
            }

            IReadOnlyList<string> tokens = AgentMcpCommandLineParser.Split(commandLine);
            command = tokens.Count > 0 ? tokens[0] : commandLine;
            arguments = tokens.Skip(1).Concat(configuredArguments).ToList();
        }

        public void ApplyConnectionSettings(AgentMcpServer server, AgentMcpConnectionSettings settings)
        {
            server.Transport = settings.Transport;
            server.Endpoint = AgentMcpTransportTypes.IsStdio(settings.Transport) ? string.Empty : settings.Endpoint;
            server.Command = AgentMcpTransportTypes.IsStdio(settings.Transport) ? settings.Command : string.Empty;
            server.Arguments = AgentMcpTransportTypes.IsStdio(settings.Transport) ? settings.Arguments : new List<string>();
            server.WorkingDirectory = AgentMcpTransportTypes.IsStdio(settings.Transport) ? settings.WorkingDirectory : string.Empty;
            server.TargetDirectory = AgentMcpTransportTypes.IsStdio(settings.Transport) ? settings.TargetDirectory : string.Empty;
            server.Environment = AgentMcpTransportTypes.IsStdio(settings.Transport)
                ? _credentialStore.StoreEnvironmentSecrets(server, settings.Environment, deleteEmptySecrets: true)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static bool HasValidConnectionSettings(AgentMcpServer server)
        {
            string transport = AgentMcpTransportTypes.Normalize(server.Transport, server.Command);
            return AgentMcpTransportTypes.IsStdio(transport)
                ? !string.IsNullOrWhiteSpace(server.Command)
                : IsValidHttpEndpoint(server.Endpoint);
        }

        private static bool IsValidHttpEndpoint(string endpoint)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return !string.IsNullOrWhiteSpace(first) ? first : second;
        }

        public bool TryBuildAuthSettings(
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

        public void ApplyAuthSettings(AgentMcpServer server, AgentMcpAuthSettings settings, bool deleteEmptySecrets)
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
    }

    internal sealed class AgentMcpAuthSettings
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

    internal sealed class AgentMcpConnectionSettings
    {
        public string Transport { get; set; } = AgentMcpTransportTypes.Http;
        public string Endpoint { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public List<string> Arguments { get; set; } = new();
        public string WorkingDirectory { get; set; } = string.Empty;
        public string TargetDirectory { get; set; } = string.Empty;
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
