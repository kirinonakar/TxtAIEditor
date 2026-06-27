using System;
using System.Collections.Generic;

namespace TxtAIEditor.Controls
{
    internal static class AgentMcpAuthTypes
    {
        public const string AuthTypeNone = "none";
        public const string AuthTypeApiKey = "apiKey";
        public const string AuthTypeOAuthBearer = "oauthBearer";
        public const string AuthTypeOAuthAuthorizationCode = "oauthAuthorizationCode";

        public static string NormalizeAuthType(string authType, Dictionary<string, string>? headers)
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

        public static bool IsOAuthAuthType(string authType)
        {
            return authType.Equals(AuthTypeOAuthBearer, StringComparison.OrdinalIgnoreCase) ||
                authType.Equals(AuthTypeOAuthAuthorizationCode, StringComparison.OrdinalIgnoreCase);
        }
    }
}
