using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpCredentialStore
    {
        private const string CredentialUserName = "TxtAIEditor_mcp";

        private readonly ICredentialService _credentialService;

        public AgentMcpCredentialStore(ICredentialService credentialService)
        {
            _credentialService = credentialService;
        }

        public Dictionary<string, string> StoreHeaderSecrets(
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
                        CredentialUserName,
                        value);
                }
                else if (deleteEmptySecrets)
                {
                    _credentialService.DeleteCredential(GetHeaderCredentialTarget(server, key));
                }
            }

            return storedHeaders;
        }

        public void MoveInlineHeaderValuesToCredential(AgentMcpServer server)
        {
            server.Headers = StoreHeaderSecrets(server, NormalizeHeaders(server.Headers));
        }

        public Dictionary<string, string> StoreEnvironmentSecrets(
            AgentMcpServer server,
            Dictionary<string, string> environment,
            bool deleteEmptySecrets = false)
        {
            var storedEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in environment)
            {
                string key = variable.Key?.Trim() ?? string.Empty;
                string value = variable.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                storedEnvironment[key] = string.Empty;
                if (!string.IsNullOrEmpty(value))
                {
                    _credentialService.WriteCredential(
                        GetEnvironmentCredentialTarget(server, key),
                        CredentialUserName,
                        value);
                }
                else if (deleteEmptySecrets)
                {
                    _credentialService.DeleteCredential(GetEnvironmentCredentialTarget(server, key));
                }
            }

            return storedEnvironment;
        }

        public void MoveInlineEnvironmentValuesToCredential(AgentMcpServer server)
        {
            server.Environment = StoreEnvironmentSecrets(server, NormalizeEnvironment(server.Environment));
        }

        public void DeleteCredentialsForRemovedHeaders(AgentMcpServer server, Dictionary<string, string> nextHeaders)
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

        public void DeleteCredentialsForRemovedEnvironment(AgentMcpServer server, Dictionary<string, string> nextEnvironment)
        {
            var nextNames = new HashSet<string>(nextEnvironment.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string oldName in server.Environment.Keys.ToList())
            {
                if (!nextNames.Contains(oldName))
                {
                    _credentialService.DeleteCredential(GetEnvironmentCredentialTarget(server, oldName));
                }
            }
        }

        public void DeleteAllCredentials(AgentMcpServer server)
        {
            foreach (string headerName in server.Headers.Keys.ToList())
            {
                _credentialService.DeleteCredential(GetHeaderCredentialTarget(server, headerName));
            }

            foreach (string variableName in server.Environment.Keys.ToList())
            {
                _credentialService.DeleteCredential(GetEnvironmentCredentialTarget(server, variableName));
            }

            DeleteOAuthCredentials(server);
        }

        public string GetHeaderSecret(AgentMcpServer server, string headerName, string fallbackValue)
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

        public string GetEnvironmentSecret(AgentMcpServer server, string variableName, string fallbackValue)
        {
            if (string.IsNullOrWhiteSpace(variableName))
            {
                return string.Empty;
            }

            string? credential = _credentialService.ReadCredential(GetEnvironmentCredentialTarget(server, variableName));
            return credential ?? fallbackValue ?? string.Empty;
        }

        public string StoreOAuthSecret(AgentMcpServer server, string secretName, string value, bool deleteEmptySecret)
        {
            value = value?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
            {
                _credentialService.WriteCredential(
                    GetOAuthCredentialTarget(server, secretName),
                    CredentialUserName,
                    value);
            }
            else if (deleteEmptySecret)
            {
                _credentialService.DeleteCredential(GetOAuthCredentialTarget(server, secretName));
            }

            return string.Empty;
        }

        public void MoveInlineOAuthSecretsToCredential(AgentMcpServer server)
        {
            server.OAuthAccessToken = StoreOAuthSecret(server, "access_token", server.OAuthAccessToken, deleteEmptySecret: false);
            server.OAuthRefreshToken = StoreOAuthSecret(server, "refresh_token", server.OAuthRefreshToken, deleteEmptySecret: false);
            server.OAuthClientSecret = StoreOAuthSecret(server, "client_secret", server.OAuthClientSecret, deleteEmptySecret: false);
        }

        public void DeleteOAuthCredentials(AgentMcpServer server)
        {
            _credentialService.DeleteCredential(GetOAuthCredentialTarget(server, "access_token"));
            _credentialService.DeleteCredential(GetOAuthCredentialTarget(server, "refresh_token"));
            _credentialService.DeleteCredential(GetOAuthCredentialTarget(server, "client_secret"));
        }

        public string GetOAuthAccessToken(AgentMcpServer server)
        {
            return GetOAuthSecret(server, "access_token", server.OAuthAccessToken);
        }

        public string GetOAuthClientSecret(AgentMcpServer server)
        {
            return GetOAuthSecret(server, "client_secret", server.OAuthClientSecret);
        }

        public string GetOAuthSecret(AgentMcpServer server, string secretName, string fallbackValue)
        {
            string? credential = _credentialService.ReadCredential(GetOAuthCredentialTarget(server, secretName));
            if (!string.IsNullOrEmpty(credential))
            {
                return credential;
            }

            return fallbackValue ?? string.Empty;
        }

        public string RedactServerSecrets(AgentMcpServer server, string text)
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

            foreach (var variable in server.Environment)
            {
                AddSecretIfPresent(secrets, GetEnvironmentSecret(server, variable.Key, variable.Value));
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

        private static Dictionary<string, string> NormalizeEnvironment(Dictionary<string, string>? environment)
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

        private static string GetEnvironmentCredentialTarget(AgentMcpServer server, string variableName)
        {
            return $"TxtAIEditor_MCP_{server.Id}_env_{SanitizeCredentialSegment(variableName)}";
        }

        private static string SanitizeCredentialSegment(string value)
        {
            string normalized = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_.-]+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(normalized) ? "header" : normalized;
        }
    }
}
