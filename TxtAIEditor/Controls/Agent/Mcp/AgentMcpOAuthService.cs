using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
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

        public AgentMcpOAuthService(
            AgentMcpCredentialStore credentialStore,
            Func<string, string, string> getString,
            Func<Task> saveAsync)
        {
            _credentialStore = credentialStore;
            _getString = getString;
            _saveAsync = saveAsync;
        }

        public async Task<string> EnsureAccessTokenAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            if (server.AuthType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase))
            {
                return _credentialStore.GetOAuthSecret(server, "access_token", server.OAuthAccessToken);
            }

            if (!server.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string accessToken = _credentialStore.GetOAuthSecret(server, "access_token", server.OAuthAccessToken);
            if (!string.IsNullOrWhiteSpace(accessToken) &&
                server.OAuthAccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return accessToken;
            }

            string refreshToken = _credentialStore.GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken);
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                try
                {
                    return await RefreshAccessTokenAsync(server, refreshToken, cancellationToken);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to refresh MCP OAuth token: {ex.Message}");
                }
            }

            return await RunBrowserLoginAsync(server, cancellationToken);
        }

        public async Task RunInitialLoginIfNeededAsync(
            AgentMcpServer? server,
            string errorTitle,
            Action<string, string> showError)
        {
            if (server == null ||
                !server.AuthType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(_credentialStore.GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken)))
            {
                return;
            }

            try
            {
                await RunBrowserLoginAsync(server, CancellationToken.None);
            }
            catch (Exception ex)
            {
                showError(errorTitle, ex.Message);
            }
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
                ["client_secret"] = _credentialStore.GetOAuthClientSecret(server)
            };
            OAuthTokenResponse token = await SendOAuthTokenRequestAsync(server.OAuthTokenEndpoint, form, cancellationToken);
            SaveOAuthTokenResponse(server, token, refreshToken);
            await _saveAsync();
            return token.AccessToken;
        }

        private async Task<string> RunBrowserLoginAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            string clientSecret = _credentialStore.GetOAuthClientSecret(server);
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
            SaveOAuthTokenResponse(
                server,
                token,
                _credentialStore.GetOAuthSecret(server, "refresh_token", server.OAuthRefreshToken));
            await _saveAsync();
            return token.AccessToken;
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
            server.OAuthAccessToken = _credentialStore.StoreOAuthSecret(server, "access_token", token.AccessToken, deleteEmptySecret: true);
            server.OAuthRefreshToken = _credentialStore.StoreOAuthSecret(
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

        private sealed class OAuthTokenResponse
        {
            public string AccessToken { get; set; } = string.Empty;
            public string RefreshToken { get; set; } = string.Empty;
            public DateTimeOffset ExpiresAt { get; set; }
        }
    }
}
