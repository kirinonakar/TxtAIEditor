using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpRegistry
    {
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly string _mcpFilePath;
        private readonly string _agentPluginsFilePath;
        private readonly string _legacyAgentPluginsFilePath;

        public List<AgentMcpServer> Servers { get; } = new();
        public List<AgentPluginPackage> Plugins { get; } = new();
        public string PluginsInstallDirectory { get; }
        public string PluginDataDirectory { get; }

        public AgentMcpRegistry(AgentMcpCredentialStore credentialStore, string? settingsDirectory = null)
        {
            _credentialStore = credentialStore;
            string settingsDir = settingsDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".TxtAIEditor");
            _mcpFilePath = Path.Combine(settingsDir, "agent-mcp-servers.json");
            PluginsInstallDirectory = Path.Combine(settingsDir, "plugins");
            _agentPluginsFilePath = Path.Combine(PluginsInstallDirectory, "installed.json");
            _legacyAgentPluginsFilePath = Path.Combine(settingsDir, "agent-plugins.json");
            PluginDataDirectory = Path.Combine(settingsDir, "plugin-data");
        }

        public async Task<bool> LoadAsync()
        {
            bool migratedPlaintextHeaders = false;
            bool migratedPlaintextOAuth = false;
            bool migratedPlaintextEnvironment = false;
            bool migratedInlineStdioCommands = false;
            bool migratedAgentPlugins = false;
            bool migratedPluginRegistry = false;
            try
            {
                if (File.Exists(_mcpFilePath))
                {
                    string json = await File.ReadAllTextAsync(_mcpFilePath);
                    var loaded = JsonSerializer.Deserialize<List<AgentMcpServer>>(
                        json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (loaded != null)
                    {
                        Servers.Clear();
                        Servers.AddRange(loaded
                            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && AgentMcpConfigurationService.HasValidConnectionSettings(s))
                            .Select(s =>
                            {
                                if (string.IsNullOrWhiteSpace(s.Id))
                                {
                                    s.Id = Guid.NewGuid().ToString("N");
                                }

                                s.Name = s.Name.Trim();
                                s.Transport = AgentMcpTransportTypes.Normalize(s.Transport, s.Command);
                                s.Stateless = !AgentMcpTransportTypes.IsStdio(s.Transport) && s.Stateless;
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
                                s.AgentPluginName = s.AgentPluginName?.Trim() ?? string.Empty;
                                s.AgentPluginId = s.AgentPluginId?.Trim() ?? string.Empty;
                                s.AgentPluginSource = s.AgentPluginSource?.Trim() ?? string.Empty;
                                s.AgentPluginServerName = s.AgentPluginServerName?.Trim() ?? string.Empty;
                                s.AgentPluginRoot = s.AgentPluginRoot?.Trim() ?? string.Empty;
                                s.AgentPluginDataDirectory = s.AgentPluginDataDirectory?.Trim() ?? string.Empty;
                                s.AuthType = s.IsAgentPlugin
                                    ? AuthTypeNone
                                    : NormalizeAuthType(s.AuthType, s.Headers);
                                migratedPlaintextHeaders |= !s.IsAgentPlugin &&
                                    s.Headers.Values.Any(value => !string.IsNullOrWhiteSpace(value));
                                migratedPlaintextOAuth |= !string.IsNullOrWhiteSpace(s.OAuthAccessToken) ||
                                    !string.IsNullOrWhiteSpace(s.OAuthRefreshToken) ||
                                    !string.IsNullOrWhiteSpace(s.OAuthClientSecret);
                                migratedPlaintextEnvironment |= !s.IsAgentPlugin &&
                                    s.Environment.Values.Any(value => !string.IsNullOrEmpty(value));
                                if (!s.IsAgentPlugin)
                                {
                                    _credentialStore.MoveInlineHeaderValuesToCredential(s);
                                    _credentialStore.MoveInlineEnvironmentValuesToCredential(s);
                                }
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

            try
            {
                Plugins.Clear();
                string pluginsFilePath = File.Exists(_agentPluginsFilePath)
                    ? _agentPluginsFilePath
                    : _legacyAgentPluginsFilePath;
                migratedPluginRegistry = !pluginsFilePath.Equals(
                    _agentPluginsFilePath,
                    StringComparison.OrdinalIgnoreCase) && File.Exists(pluginsFilePath);
                if (File.Exists(pluginsFilePath))
                {
                    string json = await File.ReadAllTextAsync(pluginsFilePath);
                    var loadedPlugins = JsonSerializer.Deserialize<List<AgentPluginPackage>>(json);
                    if (loadedPlugins != null)
                    {
                        Plugins.AddRange(loadedPlugins.Where(plugin =>
                            !string.IsNullOrWhiteSpace(plugin.Id) &&
                            !string.IsNullOrWhiteSpace(plugin.Name) &&
                            !string.IsNullOrWhiteSpace(plugin.Root)));
                    }
                }

                foreach (AgentPluginPackage plugin in Plugins)
                {
                    plugin.Name = plugin.Name?.Trim() ?? string.Empty;
                    plugin.Root = plugin.Root?.Trim() ?? string.Empty;
                    plugin.Source = plugin.Source?.Trim() ?? string.Empty;
                    plugin.Skills = plugin.Skills?
                        .Where(skill => !string.IsNullOrWhiteSpace(skill.Name) && File.Exists(skill.SkillFilePath))
                        .ToList() ?? new List<AgentPluginSkill>();
                }

                foreach (IGrouping<string, AgentMcpServer> legacyGroup in Servers
                    .Where(server => server.IsAgentPlugin && string.IsNullOrWhiteSpace(server.AgentPluginId))
                    .GroupBy(
                        server => $"{server.AgentPluginName}|{server.AgentPluginRoot}",
                        StringComparer.OrdinalIgnoreCase))
                {
                    AgentMcpServer first = legacyGroup.First();
                    var package = new AgentPluginPackage
                    {
                        Name = first.AgentPluginName,
                        Root = first.AgentPluginRoot,
                        Source = first.AgentPluginSource
                    };
                    try
                    {
                        AgentPluginLoadResult reloaded = AgentPluginLoader.Load(first.AgentPluginRoot, PluginDataDirectory);
                        package.Skills = reloaded.Skills.ToList();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to migrate Agent Plugin '{first.AgentPluginName}': {ex.Message}");
                    }

                    Plugins.Add(package);
                    migratedAgentPlugins = true;
                    foreach (AgentMcpServer server in legacyGroup)
                    {
                        server.AgentPluginId = package.Id;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load Agent Plugins: {ex.Message}");
            }

            return migratedPlaintextHeaders ||
                migratedPlaintextOAuth ||
                migratedPlaintextEnvironment ||
                migratedInlineStdioCommands ||
                migratedAgentPlugins ||
                migratedPluginRegistry;
        }

        public async Task SaveAsync()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_mcpFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(Servers, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_mcpFilePath, json);
                string? pluginsDirectory = Path.GetDirectoryName(_agentPluginsFilePath);
                if (pluginsDirectory != null)
                {
                    Directory.CreateDirectory(pluginsDirectory);
                }
                string pluginsJson = JsonSerializer.Serialize(Plugins, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_agentPluginsFilePath, pluginsJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save MCP servers: {ex.Message}");
            }
        }

        public AgentMcpServer? FindServer(string name)
        {
            return Servers.FirstOrDefault(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public string CreateUniqueServerName(string requestedName)
        {
            if (Servers.All(server => !server.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase)))
            {
                return requestedName;
            }

            int suffix = 2;
            string candidate;
            do
            {
                candidate = $"{requestedName} ({suffix++})";
            }
            while (Servers.Any(server => server.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)));

            return candidate;
        }
    }
}
