using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpOAuthService
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly Func<string, string, string> _getString;
        private readonly Func<Task> _saveAsync;
        private readonly Action<string> _launchBrowser;

        public AgentMcpOAuthService(
            AgentMcpCredentialStore credentialStore,
            Func<string, string, string> getString,
            Func<Task> saveAsync,
            Action<string>? launchBrowser = null)
        {
            _credentialStore = credentialStore;
            _getString = getString;
            _saveAsync = saveAsync;
            _launchBrowser = launchBrowser ?? LaunchBrowser;
        }

        public async Task<string> EnsureAccessTokenAsync(
            AgentMcpServer server, CancellationToken cancellationToken, string? rejectedAccessToken = null)
        {
            if (server.AuthType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase))
            {
                return _credentialStore.GetOAuthSecret(server, "access_token", server.OAuthAccessToken);
            }

            if (!server.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) &&
                !server.AuthType.Equals(AuthTypeNone, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            await server.OAuthLoginGate.WaitAsync(cancellationToken);
            try
            {
                if (server.AuthType.Equals(AuthTypeNone, StringComparison.OrdinalIgnoreCase))
                {
                    return await RunBrowserLoginAsync(server, cancellationToken);
                }

                string accessToken = GetUsableAccessToken(server);
                if (accessToken.Length > 0 && !string.Equals(accessToken, rejectedAccessToken, StringComparison.Ordinal))
                {
                    return accessToken;
                }

                string refreshToken = _credentialStore.GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken);
                if (!NeedsAutomaticOAuth(server) && !string.IsNullOrWhiteSpace(refreshToken))
                {
                    try
                    {
                        return await RefreshAccessTokenAsync(server, refreshToken, cancellationToken);
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                    {
                        Debug.WriteLine($"MCP OAuth refresh rejected with HTTP {(int?)ex.StatusCode}; signing in again.");
                    }
                }

                return await RunBrowserLoginAsync(server, cancellationToken);
            }
            finally
            {
                server.OAuthLoginGate.Release();
            }
        }

        public async Task RunInitialLoginIfNeededAsync(
            AgentMcpServer? server,
            string errorTitle,
            Action<string, string> showError)
        {
            if (server == null || AgentMcpTransportTypes.IsStdio(server.Transport))
            {
                return;
            }

            try
            {
                if (server.AuthType.Equals(AuthTypeNone, StringComparison.OrdinalIgnoreCase) ||
                    (server.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) &&
                     (NeedsAutomaticOAuth(server) || string.IsNullOrWhiteSpace(
                         _credentialStore.GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken)))))
                {
                    await EnsureAccessTokenAsync(server, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                showError(errorTitle, ex.Message);
            }
        }

        private string GetUsableAccessToken(AgentMcpServer server)
        {
            return server.OAuthAccessTokenExpiresAt > DateTimeOffset.UtcNow
                ? _credentialStore.GetOAuthAccessToken(server)
                : string.Empty;
        }

        private async Task<string> RefreshAccessTokenAsync(
            AgentMcpServer server,
            string refreshToken,
            CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = server.OAuthClientId,
                ["client_secret"] = _credentialStore.GetOAuthClientSecret(server),
                ["resource"] = GetResourceIdentifier(server.Endpoint)
            };
            OAuthTokenResponse token = await SendOAuthTokenRequestAsync(server.OAuthTokenEndpoint, form, cancellationToken);
            SaveOAuthTokenResponse(server, token, refreshToken);
            await _saveAsync();
            return token.AccessToken;
        }

        private async Task<string> RunBrowserLoginAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            int port = GetFreeTcpPort();
            string redirectUri = $"http://127.0.0.1:{port}/callback/";
            OAuthClient? registeredClient = await TryRegisterOAuthClientAsync(server, redirectUri, cancellationToken);
            if (registeredClient == null &&
                (NeedsAutomaticOAuth(server) || server.OAuthClientDynamicallyRegistered == true))
            {
                if (server.AuthType.Equals(AuthTypeNone, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                throw new InvalidOperationException(_getString(
                    "AgentMcpOAuthClientConfigRequired",
                    "OAuth Client ID, Authorization URL, Token URL을 입력해주세요. Client Secret은 공개 클라이언트에서는 생략할 수 있습니다."));
            }

            OAuthClient client = registeredClient ?? new OAuthClient(
                server.OAuthClientId, _credentialStore.GetOAuthClientSecret(server),
                server.OAuthAuthorizationEndpoint, server.OAuthTokenEndpoint, server.OAuthScopes);

            string state = CreateRandomBase64Url(32);
            string codeVerifier = CreateRandomBase64Url(64);
            string codeChallenge = CreateCodeChallenge(codeVerifier);
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            listener.Start();

            string authorizationUrl = BuildUrl(client.AuthorizationEndpoint, new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = client.Id,
                ["redirect_uri"] = redirectUri,
                ["scope"] = client.Scopes,
                ["state"] = state,
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
                ["resource"] = GetResourceIdentifier(server.Endpoint),
                ["access_type"] = "offline",
                ["prompt"] = "consent"
            });

            _launchBrowser(authorizationUrl);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            Task<HttpListenerContext> contextTask = listener.GetContextAsync();
            Task completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));
            if (completed != contextTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                ["client_id"] = client.Id,
                ["client_secret"] = client.Secret,
                ["code_verifier"] = codeVerifier,
                ["resource"] = GetResourceIdentifier(server.Endpoint)
            };
            OAuthTokenResponse token = await SendOAuthTokenRequestAsync(client.TokenEndpoint, form, cancellationToken);
            if (registeredClient != null)
            {
                server.OAuthClientSecret = _credentialStore.StoreOAuthSecret(
                    server, "client_secret", client.Secret, deleteEmptySecret: true);
                server.AuthType = AuthTypeOAuthAuthorizationCode;
                server.OAuthClientId = client.Id;
                server.OAuthAuthorizationEndpoint = client.AuthorizationEndpoint;
                server.OAuthTokenEndpoint = client.TokenEndpoint;
                server.OAuthScopes = client.Scopes;
                server.OAuthClientDynamicallyRegistered = true;
            }

            SaveOAuthTokenResponse(server, token, string.Empty);
            await _saveAsync();
            return token.AccessToken;
        }

        private static void LaunchBrowser(string authorizationUrl)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true
            });
        }

        private static bool NeedsAutomaticOAuth(AgentMcpServer server)
        {
            if (server.AuthType.Equals(AuthTypeNone, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return server.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(server.OAuthClientId) ||
                 string.IsNullOrWhiteSpace(server.OAuthAuthorizationEndpoint) ||
                 string.IsNullOrWhiteSpace(server.OAuthTokenEndpoint));
        }

        private async Task<OAuthClient?> TryRegisterOAuthClientAsync(
            AgentMcpServer server,
            string redirectUri,
            CancellationToken cancellationToken)
        {
            bool requiresRegistration = NeedsAutomaticOAuth(server) || server.OAuthClientDynamicallyRegistered == true;
            // Old settings did not record how the client ID was obtained.
            bool couldBeLegacyRegistration = server.OAuthClientDynamicallyRegistered == null &&
                string.IsNullOrWhiteSpace(_credentialStore.GetOAuthClientSecret(server));
            if (!requiresRegistration && !couldBeLegacyRegistration)
            {
                return null;
            }

            if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out Uri? resourceUri) ||
                !IsHttpScheme(resourceUri.Scheme))
            {
                return null;
            }

            using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            discoveryCts.CancelAfter(TimeSpan.FromSeconds(8));
            OAuthDiscovery? discovery;
            try
            {
                discovery = await DiscoverOAuthAsync(resourceUri, discoveryCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (HttpRequestException) when (!requiresRegistration)
            {
                return null;
            }

            if (discovery == null)
            {
                return null;
            }

            if (!requiresRegistration &&
                (!string.Equals(server.OAuthAuthorizationEndpoint, discovery.AuthorizationEndpoint, StringComparison.Ordinal) ||
                 !string.Equals(server.OAuthTokenEndpoint, discovery.TokenEndpoint, StringComparison.Ordinal) ||
                 !string.Equals(server.OAuthScopes, discovery.Scopes, StringComparison.Ordinal) ||
                 !discovery.SupportsPkce))
            {
                return null;
            }

            if (!discovery.SupportsPkce)
            {
                throw new InvalidOperationException(_getString(
                    "AgentMcpOAuthPkceUnsupported",
                    "MCP OAuth 서버가 PKCE S256을 지원한다고 알리지 않아 로그인을 진행할 수 없습니다."));
            }

            if (string.IsNullOrWhiteSpace(discovery.RegistrationEndpoint))
            {
                if (!requiresRegistration)
                {
                    return null;
                }

                throw new InvalidOperationException(_getString(
                    "AgentMcpOAuthRegistrationRequired",
                    "이 MCP 서버는 OAuth 로그인을 지원하지만 자동 클라이언트 등록을 제공하지 않습니다. OAuth 설정을 직접 입력해주세요."));
            }

            var registrationPayload = new Dictionary<string, object>
            {
                ["client_name"] = "TxtAIEditor",
                ["application_type"] = "native",
                ["redirect_uris"] = new[] { redirectUri },
                ["grant_types"] = new[] { "authorization_code" },
                ["response_types"] = new[] { "code" },
                ["token_endpoint_auth_method"] = "none"
            };
            string registrationJson = JsonSerializer.Serialize(registrationPayload);
            using var registrationContent = new StringContent(registrationJson, Encoding.UTF8, "application/json");
            using HttpResponseMessage registrationResponse = await HttpClient.PostAsync(
                discovery.RegistrationEndpoint,
                registrationContent,
                cancellationToken);
            string registrationBody = await registrationResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!registrationResponse.IsSuccessStatusCode)
            {
                string detail = string.IsNullOrWhiteSpace(registrationBody)
                    ? $"{(int)registrationResponse.StatusCode} {registrationResponse.ReasonPhrase}"
                    : registrationBody;
                throw new InvalidOperationException(string.Format(
                    _getString("AgentMcpOAuthRegistrationFailed", "OAuth 클라이언트 자동 등록에 실패했습니다: {0}"),
                    detail));
            }

            using JsonDocument registrationDocument = JsonDocument.Parse(registrationBody);
            JsonElement registrationRoot = registrationDocument.RootElement;
            string clientId = TryGetStringProperty(registrationRoot, "client_id");
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new InvalidOperationException(_getString(
                    "AgentMcpOAuthRegistrationResponseInvalid",
                    "OAuth 클라이언트 자동 등록 응답에 Client ID가 없습니다."));
            }

            return new OAuthClient(
                clientId, TryGetStringProperty(registrationRoot, "client_secret"),
                discovery.AuthorizationEndpoint, discovery.TokenEndpoint, discovery.Scopes);
        }

        private async Task<OAuthDiscovery?> DiscoverOAuthAsync(
            Uri resourceUri,
            CancellationToken cancellationToken)
        {
            OAuthProtectedResourceMetadata? resourceMetadata = null;
            foreach (Uri metadataUri in BuildProtectedResourceMetadataUris(resourceUri))
            {
                resourceMetadata = await GetProtectedResourceMetadataAsync(metadataUri, cancellationToken);
                if (resourceMetadata != null)
                {
                    break;
                }
            }

            if (resourceMetadata == null || resourceMetadata.AuthorizationServers.Count == 0)
            {
                return null;
            }

            foreach (string authorizationServer in resourceMetadata.AuthorizationServers)
            {
                foreach (Uri metadataUri in BuildAuthorizationServerMetadataUris(authorizationServer))
                {
                    OAuthAuthorizationServerMetadata? authorizationMetadata =
                        await GetAuthorizationServerMetadataAsync(metadataUri, cancellationToken);
                    if (authorizationMetadata == null)
                    {
                        continue;
                    }

                    string scopes = resourceMetadata.Scopes.Count > 0
                        ? string.Join(" ", resourceMetadata.Scopes)
                        : string.Join(" ", authorizationMetadata.Scopes);
                    return new OAuthDiscovery
                    {
                        AuthorizationEndpoint = authorizationMetadata.AuthorizationEndpoint,
                        TokenEndpoint = authorizationMetadata.TokenEndpoint,
                        RegistrationEndpoint = authorizationMetadata.RegistrationEndpoint,
                        Scopes = scopes,
                        SupportsPkce = authorizationMetadata.CodeChallengeMethods.Contains("S256", StringComparer.OrdinalIgnoreCase)
                    };
                }
            }

            return null;
        }

        private static async Task<OAuthProtectedResourceMetadata?> GetProtectedResourceMetadataAsync(
            Uri metadataUri,
            CancellationToken cancellationToken)
        {
            using JsonDocument? document = await GetJsonDocumentAsync(metadataUri, cancellationToken);
            if (document == null)
            {
                return null;
            }

            JsonElement root = document.RootElement;
            var authorizationServers = GetStringArrayProperty(root, "authorization_servers");
            if (authorizationServers.Count == 0)
            {
                string authorizationServer = TryGetStringProperty(root, "authorization_server");
                if (!string.IsNullOrWhiteSpace(authorizationServer))
                {
                    authorizationServers.Add(authorizationServer);
                }
            }

            return authorizationServers.Count == 0
                ? null
                : new OAuthProtectedResourceMetadata
                {
                    AuthorizationServers = authorizationServers,
                    Scopes = GetStringArrayProperty(root, "scopes_supported")
                };
        }

        private static async Task<OAuthAuthorizationServerMetadata?> GetAuthorizationServerMetadataAsync(
            Uri metadataUri,
            CancellationToken cancellationToken)
        {
            using JsonDocument? document = await GetJsonDocumentAsync(metadataUri, cancellationToken);
            if (document == null)
            {
                return null;
            }

            JsonElement root = document.RootElement;
            string authorizationEndpoint = TryGetStringProperty(root, "authorization_endpoint");
            string tokenEndpoint = TryGetStringProperty(root, "token_endpoint");
            if (string.IsNullOrWhiteSpace(authorizationEndpoint) || string.IsNullOrWhiteSpace(tokenEndpoint))
            {
                return null;
            }

            return new OAuthAuthorizationServerMetadata
            {
                AuthorizationEndpoint = authorizationEndpoint,
                TokenEndpoint = tokenEndpoint,
                RegistrationEndpoint = TryGetStringProperty(root, "registration_endpoint"),
                Scopes = GetStringArrayProperty(root, "scopes_supported"),
                CodeChallengeMethods = GetStringArrayProperty(root, "code_challenge_methods_supported")
            };
        }

        private static async Task<JsonDocument?> GetJsonDocumentAsync(
            Uri metadataUri,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, metadataUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IEnumerable<Uri> BuildProtectedResourceMetadataUris(Uri resourceUri)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string origin = $"{resourceUri.Scheme}://{resourceUri.Authority}";
            string path = resourceUri.AbsolutePath.Trim('/');
            AddCandidate($"{origin}/.well-known/oauth-protected-resource");
            if (!string.IsNullOrWhiteSpace(path))
            {
                AddCandidate($"{origin}/.well-known/oauth-protected-resource/{path}");
                AddCandidate($"{origin}/{path}/.well-known/oauth-protected-resource");
            }

            foreach (string candidate in candidates)
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
                {
                    yield return uri;
                }
            }

            void AddCandidate(string candidate)
            {
                candidates.Add(candidate.TrimEnd('/'));
            }
        }

        private static IEnumerable<Uri> BuildAuthorizationServerMetadataUris(string issuer)
        {
            if (!Uri.TryCreate(issuer, UriKind.Absolute, out Uri? issuerUri) ||
                !IsHttpScheme(issuerUri.Scheme))
            {
                yield break;
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string origin = $"{issuerUri.Scheme}://{issuerUri.Authority}";
            string path = issuerUri.AbsolutePath.Trim('/');
            AddCandidate($"{origin}/.well-known/oauth-authorization-server");
            AddCandidate($"{origin}/.well-known/openid-configuration");
            if (!string.IsNullOrWhiteSpace(path))
            {
                AddCandidate($"{origin}/.well-known/oauth-authorization-server/{path}");
                AddCandidate($"{origin}/.well-known/openid-configuration/{path}");
            }

            foreach (string candidate in candidates)
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
                {
                    yield return uri;
                }
            }

            void AddCandidate(string candidate)
            {
                candidates.Add(candidate.TrimEnd('/'));
            }
        }

        private static async Task<OAuthTokenResponse> SendOAuthTokenRequestAsync(
            string tokenEndpoint,
            Dictionary<string, string> form,
            CancellationToken cancellationToken)
        {
            using var content = new FormUrlEncodedContent(form.Where(item => !string.IsNullOrWhiteSpace(item.Value)));
            using HttpResponseMessage response = await HttpClient.PostAsync(tokenEndpoint, content, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {json}", null, response.StatusCode);
            }

            using JsonDocument document = JsonDocument.Parse(json);
            string accessToken = TryGetStringProperty(document.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("OAuth token response did not include an access token.");
            }

            int expiresIn = 3600;
            if (document.RootElement.TryGetProperty("expires_in", out var expiresElement) &&
                expiresElement.ValueKind == JsonValueKind.Number &&
                expiresElement.TryGetInt32(out int parsedExpiresIn))
            {
                expiresIn = parsedExpiresIn;
            }

            return new OAuthTokenResponse
            {
                AccessToken = accessToken,
                RefreshToken = TryGetStringProperty(document.RootElement, "refresh_token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, expiresIn))
            };
        }

        private void SaveOAuthTokenResponse(AgentMcpServer server, OAuthTokenResponse token, string existingRefreshToken)
        {
            server.OAuthAccessToken = _credentialStore.StoreOAuthSecret(server, "access_token", token.AccessToken, deleteEmptySecret: true);
            server.OAuthRefreshToken = _credentialStore.StoreOAuthSecret(
                server,
                "refresh_token",
                string.IsNullOrWhiteSpace(token.RefreshToken) ? existingRefreshToken : token.RefreshToken,
                deleteEmptySecret: true);
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

        private static string TryGetStringProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return string.Empty;
            }

            return property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static List<string> GetStringArrayProperty(JsonElement element, string propertyName)
        {
            var values = new List<string>();
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (JsonElement value in property.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    values.Add(value.GetString()!);
                }
            }

            return values;
        }

        private static bool IsHttpScheme(string scheme)
        {
            return scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetResourceIdentifier(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !IsHttpScheme(uri.Scheme))
            {
                return endpoint?.Trim() ?? string.Empty;
            }

            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty
            };
            string resource = builder.Uri.AbsoluteUri;
            if (builder.Uri.AbsolutePath != "/")
            {
                resource = resource.TrimEnd('/');
            }

            return resource;
        }

        private sealed record OAuthClient(
            string Id, string Secret, string AuthorizationEndpoint, string TokenEndpoint, string Scopes);

        private sealed class OAuthTokenResponse
        {
            public string AccessToken { get; set; } = string.Empty;
            public string RefreshToken { get; set; } = string.Empty;
            public DateTimeOffset ExpiresAt { get; set; }
        }

        private sealed class OAuthDiscovery
        {
            public string AuthorizationEndpoint { get; set; } = string.Empty;
            public string TokenEndpoint { get; set; } = string.Empty;
            public string RegistrationEndpoint { get; set; } = string.Empty;
            public string Scopes { get; set; } = string.Empty;
            public bool SupportsPkce { get; set; }
        }

        private sealed class OAuthProtectedResourceMetadata
        {
            public List<string> AuthorizationServers { get; set; } = new();
            public List<string> Scopes { get; set; } = new();
        }

        private sealed class OAuthAuthorizationServerMetadata
        {
            public string AuthorizationEndpoint { get; set; } = string.Empty;
            public string TokenEndpoint { get; set; } = string.Empty;
            public string RegistrationEndpoint { get; set; } = string.Empty;
            public List<string> Scopes { get; set; } = new();
            public List<string> CodeChallengeMethods { get; set; } = new();
        }
    }
}
