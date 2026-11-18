using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;
using static TxtAIEditor.Controls.AgentMcpPluginManagerTool;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpPluginService
    {
        private readonly AgentMcpRegistry _registry;
        private readonly AgentMcpConnectionService _connections;
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly AgentSessionSelectionContext _selections;
        private HashSet<string> _selectedServerIds => _selections.Current.McpServers;
        private HashSet<string> _selectedAgentPluginIds => _selections.Current.McpPlugins;
        private readonly SemaphoreSlim _installGate = new(1, 1);
        private readonly AgentMcpDialogService _dialogService;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Func<Task> _saveAsync;
        private readonly Action _rebuildAliases;
        private readonly Action _updateUI;
        private readonly AgentSkillController _skillController;

        public bool HasSelectedPlugins => _selectedAgentPluginIds.Count > 0;

        public AgentMcpPluginService(
            AgentMcpRegistry registry,
            AgentMcpConnectionService connections,
            AgentMcpCredentialStore credentialStore,
            AgentSessionSelectionContext selections,
            AgentMcpDialogService dialogService,
            Action<object> initializePickerWindow,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Func<Task> saveAsync,
            Action rebuildAliases,
            Action updateUI,
            AgentSkillController skillController)
        {
            _registry = registry;
            _connections = connections;
            _credentialStore = credentialStore;
            _selections = selections;
            _dialogService = dialogService;
            _initializePickerWindow = initializePickerWindow;
            _showError = showError;
            _getString = getString;
            _saveAsync = saveAsync;
            _rebuildAliases = rebuildAliases;
            _updateUI = updateUI;
            _skillController = skillController;
        }

        public void PruneSelection()
        {
            _selectedAgentPluginIds.RemoveWhere(id =>
                _registry.Plugins.All(plugin => !plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        }

        public async Task<bool> ReloadRegistryAsync()
        {
            await _installGate.WaitAsync();
            try
            {
                return await _registry.LoadAsync();
            }
            finally
            {
                _installGate.Release();
            }
        }

        public void ClearSelection()
        {
            foreach (AgentPluginPackage plugin in _registry.Plugins
                .Where(plugin => _selectedAgentPluginIds.Contains(plugin.Id))
                .ToList())
            {
                DeactivateAgentPlugin(plugin);
            }
        }

        public async Task ImportAgentPluginAsync()
        {
            AgentPluginImportSourceKind? sourceKind = await _dialogService.ShowAgentPluginImportSourceAsync();
            if (sourceKind == null)
            {
                return;
            }

            if (sourceKind == AgentPluginImportSourceKind.GitHub)
            {
                await InstallAgentPluginFromGitHubAsync();
                return;
            }

            if (sourceKind == AgentPluginImportSourceKind.ZipArchive)
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.Downloads
                };
                picker.FileTypeFilter.Add(".zip");
                _initializePickerWindow(picker);
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    await ImportAgentPluginArchiveAsync(file.Path);
                }

                return;
            }

            var folderPicker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            folderPicker.FileTypeFilter.Add("*");
            _initializePickerWindow(folderPicker);
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            try
            {
                AgentPluginLoadResult sourceResult = AgentPluginLoader.Load(folder.Path, _registry.PluginDataDirectory);
                string localSource = "local:" + sourceResult.PluginName.ToLowerInvariant();
                StopPluginServersBySource(localSource);
                string installedRoot = AgentPluginLocalInstaller.Install(
                    folder.Path,
                    Path.Combine(_registry.PluginsInstallDirectory, "local"),
                    sourceResult.PluginName);
                AgentPluginLoadResult result = AgentPluginLoader.Load(installedRoot, _registry.PluginDataDirectory);
                ApplyAgentPluginResult(result, localSource);
                await _saveAsync();

                if (result.Warnings.Count > 0)
                {
                    _showError(
                        _getString("AgentPluginImportWarningTitle", "Agent Plugin 가져오기 경고"),
                        string.Join(Environment.NewLine, result.Warnings));
                }
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentPluginImportErrorTitle", "Agent Plugin 가져오기 오류"),
                    string.Format(
                        _getString("AgentPluginImportErrorMessage", "Agent Plugin을 가져오는 중 오류가 발생했습니다: {0}"),
                        ex.Message));
            }
        }

        private async Task ImportAgentPluginArchiveAsync(string archivePath)
        {
            try
            {
                AgentPluginArchiveInstallResult installed = await AgentPluginArchiveInstaller.InstallAsync(
                    archivePath,
                    Path.Combine(_registry.PluginsInstallDirectory, "local"),
                    _registry.PluginDataDirectory,
                    StopPluginServersBySource,
                    CancellationToken.None);

                var warnings = new List<string>();
                int mcpServerCount = 0;
                int skillCount = 0;
                foreach (AgentPluginArchiveInstalledPlugin plugin in installed.Plugins)
                {
                    AgentPluginLoadResult loaded = AgentPluginLoader.Load(
                        plugin.PluginRoot,
                        _registry.PluginDataDirectory);
                    ApplyAgentPluginResult(loaded, plugin.SourceKey);
                    warnings.AddRange(loaded.Warnings);
                    mcpServerCount += loaded.Servers.Count;
                    skillCount += loaded.Skills.Count;
                }

                NotifyActivePluginSkills();
                await _saveAsync();
                await _dialogService.ShowAgentPluginInstallResultAsync(
                    installed.DisplayName,
                    installed.Plugins.Count,
                    mcpServerCount,
                    skillCount,
                    warnings);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentPluginImportErrorTitle", "Agent Plugin 가져오기 오류"),
                    string.Format(
                        _getString("AgentPluginImportErrorMessage", "Agent Plugin을 가져오는 중 오류가 발생했습니다: {0}"),
                        ex.Message));
            }
        }

        public async Task InstallAgentPluginFromGitHubAsync()
        {
            string? sourceUrl = await _dialogService.ShowAgentPluginGitHubInstallAsync();
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                return;
            }

            try
            {
                AgentPluginGitHubSource source = AgentPluginGitHubInstaller.ParseSource(sourceUrl);
                string installBase = Path.Combine(_registry.PluginsInstallDirectory, "github");
                List<AgentPluginPackage> packages;
                List<string> warnings;
                await _installGate.WaitAsync();
                try
                {
                    (packages, warnings) = await InstallGitHubPackagesAsync(
                        source, installBase, source.SourceKey, CancellationToken.None);
                    await _saveAsync();
                }
                finally
                {
                    _installGate.Release();
                }

                await _dialogService.ShowAgentPluginInstallResultAsync(
                    source.DisplayName,
                    packages.Count,
                    packages.Sum(package => GetPluginServers(package).Count),
                    packages.Sum(package => package.Skills.Count),
                    warnings);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentPluginGitHubInstallErrorTitle", "GitHub Agent Plugin 설치 오류"),
                    string.Format(
                        _getString("AgentPluginGitHubInstallErrorMessage", "GitHub에서 Agent Plugin을 설치하는 중 오류가 발생했습니다: {0}"),
                        ex.Message));
            }
        }

        public async Task<AgentPluginInstallResult> InstallFromAgentAsync(
            string type, JsonElement arguments, CancellationToken cancellationToken)
        {
            await _installGate.WaitAsync(cancellationToken);
            try
            {
                if (type == "mcp")
                {
                    return await RegisterMcpAsync(arguments, cancellationToken);
                }

                string sourceUrl = GetString(arguments, "source").Trim();
                if (type == "skill")
                {
                    string name = await AgentPluginGitHubInstaller.InstallSkillAsync(
                        sourceUrl, GetString(arguments, "name").Trim(), AgentSkillDirectories.UserSkillsDirectory,
                        cancellationToken);
                    await _skillController.LoadAsync();
                    IReadOnlyList<AgentPluginEntry> skills = await _skillController.GetPluginManagerEntriesAsync();
                    return new AgentPluginInstallResult(
                        skills.Where(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray(), []);
                }

                AgentPluginGitHubSource source = AgentPluginGitHubInstaller.ParseSource(sourceUrl);
                string installBase = Path.Combine(_registry.PluginsInstallDirectory, "github");
                string sourceKey = source.SourceKey;
                if (source.Subdirectory.Length > 0)
                {
                    sourceKey += "/" + source.Subdirectory.Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();
                }
                if (source.Reference.Length > 0)
                {
                    sourceKey += "#" + source.Reference.ToLowerInvariant();
                }
                if (sourceKey != source.SourceKey)
                {
                    // Keep explicitly selected folders and revisions in separate checkouts.
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)));
                    installBase = Path.Combine(_registry.PluginsInstallDirectory, "github-sources", hash);
                }

                var packages = _registry.Plugins
                    .Where(package => package.Source.Equals(sourceKey, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(Path.Combine(package.Root, "plugin.json")))
                    .ToList();
                bool alreadyInstalled = packages.Count > 0;
                List<string> warnings = [];
                if (!alreadyInstalled)
                {
                    (packages, warnings) = await InstallGitHubPackagesAsync(
                        source, installBase, sourceKey, cancellationToken);
                }

                await _registry.SaveAsync(throwOnError: true);
                _rebuildAliases();
                _updateUI();
                return new AgentPluginInstallResult(packages.Select(GetPluginEntry).ToArray(), warnings, alreadyInstalled);
            }
            finally
            {
                _installGate.Release();
            }
        }

        private async Task<AgentPluginInstallResult> RegisterMcpAsync(
            JsonElement arguments, CancellationToken cancellationToken)
        {
            string name = GetString(arguments, "name").Trim();
            string endpoint = GetString(arguments, "url").Trim();
            string command = GetString(arguments, "command").Trim();
            string directory = GetString(arguments, "workingDirectory").Trim();
            bool stateless = GetBoolean(arguments, "stateless", false);
            if (name.Length == 0 || name.Any(char.IsControl) || (endpoint.Length == 0) == (command.Length == 0))
            {
                throw new ArgumentException("Provide an MCP name and exactly one of url (HTTP) or command (stdio).");
            }

            if (IsServerName(name) || _registry.Plugins.Any(plugin =>
                GetAgentPluginMenuName(plugin).Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("That MCP name is already in use. Choose another name.");
            }

            var server = new AgentMcpServer
            {
                Name = name,
                Description = GetString(arguments, "description").Trim(),
                Transport = command.Length > 0 ? AgentMcpTransportTypes.Stdio : AgentMcpTransportTypes.Http,
                Endpoint = endpoint,
                Command = command,
                WorkingDirectory = directory,
                Stateless = stateless,
                AuthType = AuthTypeNone
            };
            if (command.Length == 0)
            {
                if (!AgentMcpConfigurationService.HasValidConnectionSettings(server) ||
                    !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
                    uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
                    arguments.TryGetProperty("args", out _) || arguments.TryGetProperty("env", out _) || directory.Length > 0)
                {
                    throw new ArgumentException("Use a valid HTTP(S) MCP endpoint without embedded credentials or stdio options.");
                }
            }
            else
            {
                if (command.Contains('"') || command.Any(char.IsControl) ||
                    (!Path.IsPathFullyQualified(command) &&
                        (command.Any(char.IsWhiteSpace) || command.IndexOfAny(['/', '\\']) >= 0)) || stateless)
                {
                    throw new ArgumentException("Use a command name or absolute executable path, and pass each stdio argument in args.");
                }
                if (Path.IsPathFullyQualified(command) && !File.Exists(command))
                {
                    throw new ArgumentException("The MCP executable was not found at command. Verify its local path.");
                }
                if (directory.Length > 0 && (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)))
                {
                    throw new ArgumentException("workingDirectory must be an existing absolute path.");
                }
                if (arguments.TryGetProperty("args", out JsonElement args))
                {
                    if (args.ValueKind != JsonValueKind.Array || args.EnumerateArray().Any(arg =>
                        arg.ValueKind != JsonValueKind.String || arg.GetString()!.Contains('\0')))
                    {
                        throw new ArgumentException("args must be an array of strings.");
                    }
                    server.Arguments = args.EnumerateArray().Select(arg => arg.GetString()!).ToList();
                }
            }

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (arguments.TryGetProperty("env", out JsonElement env))
            {
                if (env.ValueKind != JsonValueKind.Object || env.EnumerateObject().Any(variable =>
                    string.IsNullOrWhiteSpace(variable.Name) || variable.Name.Contains('=') || variable.Name.Any(char.IsControl) ||
                    variable.Value.ValueKind != JsonValueKind.String || variable.Value.GetString()!.Contains('\0')))
                {
                    throw new ArgumentException("env must be an object of environment variable names and string values.");
                }
                foreach (var variable in env.EnumerateObject())
                {
                    environment[variable.Name] = variable.Value.GetString()!;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            AgentMcpServer? existing = _registry.FindServer(name);
            if (existing != null)
            {
                if (!existing.Endpoint.Equals(endpoint, StringComparison.Ordinal) ||
                    !existing.Command.Equals(command, StringComparison.OrdinalIgnoreCase) ||
                    !existing.WorkingDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                    existing.Stateless != stateless || !existing.Arguments.SequenceEqual(server.Arguments) ||
                    environment.Any(variable => !existing.Environment.TryGetValue(variable.Key, out string? value) ||
                        _credentialStore.GetEnvironmentSecret(existing, variable.Key, value) != variable.Value))
                {
                    throw new ArgumentException($"An MCP named '{name}' already exists with different settings. Edit it in the MCP menu or choose a different name.");
                }
                return new AgentPluginInstallResult(new[] { GetMcpEntry(existing) }, [], AlreadyInstalled: true);
            }

            // Populate the keys before saving credentials so a partially failed write can be cleaned up.
            server.Environment = environment.ToDictionary(variable => variable.Key, _ => string.Empty,
                StringComparer.OrdinalIgnoreCase);
            try
            {
                server.Environment = _credentialStore.StoreEnvironmentSecrets(server, environment);
                _registry.Servers.Add(server);
                await _registry.SaveAsync(throwOnError: true, serversOnly: true);
            }
            catch
            {
                _registry.Servers.Remove(server);
                _credentialStore.DeleteAllCredentials(server);
                throw;
            }

            _rebuildAliases();
            _updateUI();
            return new AgentPluginInstallResult(new[] { GetMcpEntry(server) }, []);
        }

        private async Task<(List<AgentPluginPackage> Packages, List<string> Warnings)> InstallGitHubPackagesAsync(
            AgentPluginGitHubSource source, string installBase, string sourceKey, CancellationToken cancellationToken)
        {
            IReadOnlyList<string> pluginRoots = await AgentPluginGitHubInstaller.InstallAsync(
                source, installBase, () => StopPluginServersBySource(sourceKey), cancellationToken);
            var packages = new List<AgentPluginPackage>();
            var warnings = new List<string>();
            foreach (string pluginRoot in pluginRoots)
            {
                AgentPluginLoadResult loaded = AgentPluginLoader.Load(pluginRoot, _registry.PluginDataDirectory);
                packages.Add(ApplyAgentPluginResult(loaded, sourceKey));
                warnings.AddRange(loaded.Warnings);
            }

            foreach (AgentPluginPackage stalePackage in _registry.Plugins
                .Where(package => package.Source.Equals(sourceKey, StringComparison.OrdinalIgnoreCase) && !packages.Contains(package))
                .ToList())
            {
                RemoveAgentPluginPackage(stalePackage);
            }
            return (packages, warnings);
        }

        public AgentPluginEntry GetMcpEntry(AgentMcpServer server)
        {
            string status = _credentialStore.RedactServerSecrets(server, _connections.GetStatus(server.Id));
            if (status.Length > 1200)
            {
                status = status[..1200] + "…";
            }
            return new AgentPluginEntry("mcp:" + server.Id, "mcp", server.Name,
                string.IsNullOrWhiteSpace(server.Description) ? server.AgentPluginName : server.Description,
                _selectedServerIds.Contains(server.Id), status, _connections.GetTools(server.Id).Count);
        }

        public AgentPluginEntry GetPluginEntry(AgentPluginPackage package) =>
            new("plugin:" + package.Id, "plugin", GetAgentPluginMenuName(package), package.Source,
                IsPluginMcpSelectionComplete(package));

        private AgentPluginPackage ApplyAgentPluginResult(AgentPluginLoadResult result, string source)
        {
            AgentPluginPackage? package = _registry.Plugins.FirstOrDefault(plugin =>
                plugin.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                plugin.Name.Equals(result.PluginName, StringComparison.OrdinalIgnoreCase));
            if (package == null)
            {
                package = new AgentPluginPackage
                {
                    Name = result.PluginName,
                    Root = result.PluginRoot,
                    Source = source
                };
                _registry.Plugins.Add(package);
            }

            package.Name = result.PluginName;
            package.Root = result.PluginRoot;
            package.Source = source;
            package.Skills = result.Skills.ToList();
            string packageDataDirectory = Path.Combine(_registry.PluginDataDirectory, package.Id);
            Directory.CreateDirectory(packageDataDirectory);

            var importedNames = result.Servers
                .Select(server => server.AgentPluginServerName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (AgentMcpServer staleServer in GetPluginServers(package)
                .Where(server => !importedNames.Contains(server.AgentPluginServerName))
                .ToList())
            {
                _credentialStore.DeleteAllCredentials(staleServer);
                _selectedServerIds.Remove(staleServer.Id);
                _connections.Stop(staleServer.Id);
                _registry.Servers.Remove(staleServer);
            }

            foreach (AgentMcpServer imported in result.Servers)
            {
                imported.AgentPluginDataDirectory = packageDataDirectory;
                AgentMcpServer? existing = _registry.Servers.FirstOrDefault(server =>
                    server.AgentPluginId.Equals(package.Id, StringComparison.OrdinalIgnoreCase) &&
                    server.AgentPluginServerName.Equals(imported.AgentPluginServerName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    imported.Name = _registry.CreateUniqueServerName(imported.Name);
                    imported.AgentPluginId = package.Id;
                    imported.AgentPluginSource = source;
                    _registry.Servers.Add(imported);
                    if (_selectedAgentPluginIds.Contains(package.Id))
                    {
                        _selectedServerIds.Add(imported.Id);
                    }
                    continue;
                }

                existing.Transport = imported.Transport;
                existing.Stateless = imported.Stateless;
                existing.Endpoint = imported.Endpoint;
                existing.Command = imported.Command;
                existing.Arguments = imported.Arguments;
                existing.WorkingDirectory = imported.WorkingDirectory;
                existing.TargetDirectory = string.Empty;
                existing.Environment = imported.Environment;
                existing.Headers = imported.Headers;
                existing.AuthType = AuthTypeNone;
                existing.AgentPluginName = imported.AgentPluginName;
                existing.AgentPluginId = package.Id;
                existing.AgentPluginSource = source;
                existing.AgentPluginServerName = imported.AgentPluginServerName;
                existing.AgentPluginRoot = imported.AgentPluginRoot;
                existing.AgentPluginDataDirectory = imported.AgentPluginDataDirectory;
                _connections.Reset(existing.Id);
            }

            NotifyActivePluginSkills();
            return package;
        }

        public async Task ToggleAgentPluginAsync(AgentPluginPackage plugin)
        {
            if (IsPluginMcpSelectionComplete(plugin))
            {
                DeactivateAgentPlugin(plugin);
                _rebuildAliases();
                _updateUI();
                return;
            }

            ActivateAgentPlugin(plugin);
            _updateUI();
            foreach (AgentMcpServer server in GetPluginServers(plugin))
            {
                await _connections.StartAsync(server, CancellationToken.None);
            }
        }

        public void ActivateAgentPlugin(AgentPluginPackage plugin)
        {
            _selectedAgentPluginIds.Add(plugin.Id);
            foreach (AgentMcpServer server in GetPluginServers(plugin))
            {
                _selectedServerIds.Add(server.Id);
            }
            NotifyActivePluginSkills();
        }

        public void DeactivateAgentPlugin(AgentPluginPackage plugin)
        {
            _selectedAgentPluginIds.Remove(plugin.Id);
            foreach (AgentMcpServer server in GetPluginServers(plugin))
            {
                _selectedServerIds.Remove(server.Id);
                _connections.StopIfUnused(server.Id);
            }

            NotifyActivePluginSkills();
        }

        public void RemoveAgentPluginPackage(AgentPluginPackage plugin)
        {
            DeactivateAgentPlugin(plugin);
            foreach (AgentMcpServer server in GetPluginServers(plugin).ToList())
            {
                _connections.Stop(server.Id);
                _credentialStore.DeleteAllCredentials(server);
                _registry.Servers.Remove(server);
            }

            _registry.Plugins.Remove(plugin);
            NotifyActivePluginSkills();
        }

        private void StopPluginServersBySource(string source)
        {
            foreach (AgentMcpServer server in _registry.Servers.Where(server =>
                server.AgentPluginSource.Equals(source, StringComparison.OrdinalIgnoreCase)))
            {
                _connections.Stop(server.Id);
            }
        }

        public void NotifyActivePluginSkills(bool preserveOverrides = false)
        {
            _skillController.SetInstalledPluginSkills(_registry.Plugins.SelectMany(plugin =>
                plugin.Skills.Select(skill => new AgentPluginSkill
                {
                    Name = $"{GetAgentPluginMenuName(plugin)}/{skill.Name}",
                    Description = skill.Description,
                    SkillFilePath = skill.SkillFilePath
                })));

            var activeSkills = new List<AgentPluginSkill>();
            foreach (AgentPluginPackage plugin in _registry.Plugins.Where(plugin =>
                _selectedAgentPluginIds.Contains(plugin.Id)))
            {
                activeSkills.AddRange(plugin.Skills.Select(skill => new AgentPluginSkill
                {
                    Name = $"{GetAgentPluginMenuName(plugin)}/{skill.Name}",
                    Description = skill.Description,
                    SkillFilePath = skill.SkillFilePath
                }));
            }

            _skillController.SetActivePluginSkills(activeSkills, preserveOverrides);
        }

        public List<AgentMcpServer> GetPluginServers(AgentPluginPackage plugin)
        {
            return _registry.Servers
                .Where(server => server.AgentPluginId.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public bool IsPluginMcpSelectionComplete(AgentPluginPackage plugin)
        {
            return _selectedAgentPluginIds.Contains(plugin.Id) &&
                GetPluginServers(plugin).All(server => _selectedServerIds.Contains(server.Id));
        }

        public bool ShowIndividualServerChip(AgentMcpServer server)
        {
            AgentPluginPackage? plugin = _registry.Plugins.FirstOrDefault(item =>
                item.Id.Equals(server.AgentPluginId, StringComparison.OrdinalIgnoreCase));
            return plugin == null || !IsPluginMcpSelectionComplete(plugin);
        }

        public AgentPluginPackage? FindAgentPluginByMenuName(string menuName)
        {
            return _registry.Plugins.FirstOrDefault(plugin =>
                GetAgentPluginMenuName(plugin).Equals(menuName, StringComparison.OrdinalIgnoreCase));
        }

        public static string GetAgentPluginMenuName(AgentPluginPackage plugin)
        {
            string sourceLabel = plugin.Source.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
                ? plugin.Source.Substring("github:".Length)
                : Path.GetFileName(plugin.Root);
            return string.IsNullOrWhiteSpace(sourceLabel)
                ? plugin.Name
                : $"{plugin.Name} [{sourceLabel}]";
        }
    }
}
