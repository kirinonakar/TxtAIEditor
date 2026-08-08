using System;
using System.Collections.Generic;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpServer
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Transport { get; set; } = AgentMcpTransportTypes.Http;
        public string Endpoint { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public List<string> Arguments { get; set; } = new();
        public string WorkingDirectory { get; set; } = string.Empty;
        public string TargetDirectory { get; set; } = string.Empty;
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
        public string AgentPluginName { get; set; } = string.Empty;
        public string AgentPluginId { get; set; } = string.Empty;
        public string AgentPluginSource { get; set; } = string.Empty;
        public string AgentPluginServerName { get; set; } = string.Empty;
        public string AgentPluginRoot { get; set; } = string.Empty;
        public string AgentPluginDataDirectory { get; set; } = string.Empty;

        public bool IsAgentPlugin => !string.IsNullOrWhiteSpace(AgentPluginName);
    }

    internal sealed class AgentPluginSkill
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SkillFilePath { get; set; } = string.Empty;
    }

    internal sealed class AgentPluginPackage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Root { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public List<AgentPluginSkill> Skills { get; set; } = new();
    }

    public sealed class AgentMcpItem
    {
        public string Name { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
        public bool IsBuiltIn { get; set; }
        public bool IsAgentPlugin { get; set; }
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
}
