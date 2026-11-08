using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpPluginService
    {
        private readonly AgentMcpRegistry _registry;
        private readonly AgentMcpConnectionService _connections;
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly ISet<string> _selectedServerIds;
        private readonly HashSet<string> _selectedAgentPluginIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly AgentMcpDialogService _dialogService;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Func<Task> _saveAsync;
        private readonly Action _rebuildAliases;
        private readonly Action _updateUI;
        private readonly AgentSkillController _skillController;
        private readonly Action<IReadOnlyList<AgentPluginSkill>>? _activePluginSkillsChanged;

        public bool HasSelectedPlugins => _selectedAgentPluginIds.Count > 0;

        public AgentMcpPluginService(
            AgentMcpRegistry registry,
            AgentMcpConnectionService connections,
            AgentMcpCredentialStore credentialStore,
            ISet<string> selectedServerIds,
            AgentMcpDialogService dialogService,
            Action<object> initializePickerWindow,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Func<Task> saveAsync,
            Action rebuildAliases,
            Action updateUI,
            AgentSkillController skillController,
            Action<IReadOnlyList<AgentPluginSkill>>? activePluginSkillsChanged)
        {
            _registry = registry;
            _connections = connections;
            _credentialStore = credentialStore;
            _selectedServerIds = selectedServerIds;
            _dialogService = dialogService;
            _initializePickerWindow = initializePickerWindow;
            _showError = showError;
            _getString = getString;
            _saveAsync = saveAsync;
            _rebuildAliases = rebuildAliases;
            _updateUI = updateUI;
            _skillController = skillController;
            _activePluginSkillsChanged = activePluginSkillsChanged;
        }

        public void PruneSelection()
        {
            _selectedAgentPluginIds.RemoveWhere(id =>
                _registry.Plugins.All(plugin => !plugin.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
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
                AgentPluginGitHubInstallResult installed = await AgentPluginGitHubInstaller.InstallAsync(
                    sourceUrl,
                    Path.Combine(_registry.PluginsInstallDirectory, "github"),
                    StopPluginServersBySource,
                    CancellationToken.None);

                var installedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var warnings = new List<string>();
                int mcpServerCount = 0;
                int skillCount = 0;
                foreach (string pluginRoot in installed.PluginRoots)
                {
                    AgentPluginLoadResult loaded = AgentPluginLoader.Load(pluginRoot, _registry.PluginDataDirectory);
                    AgentPluginPackage package = ApplyAgentPluginResult(loaded, installed.SourceKey);
                    installedPackageIds.Add(package.Id);
                    warnings.AddRange(loaded.Warnings);
                    mcpServerCount += loaded.Servers.Count;
                    skillCount += loaded.Skills.Count;
                }

                foreach (AgentPluginPackage stalePackage in _registry.Plugins
                    .Where(plugin => plugin.Source.Equals(installed.SourceKey, StringComparison.OrdinalIgnoreCase) &&
                        !installedPackageIds.Contains(plugin.Id))
                    .ToList())
                {
                    RemoveAgentPluginPackage(stalePackage);
                }

                NotifyActivePluginSkills();
                await _saveAsync();
                await _dialogService.ShowAgentPluginInstallResultAsync(
                    installed.DisplayName,
                    installedPackageIds.Count,
                    mcpServerCount,
                    skillCount,
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

            _selectedAgentPluginIds.Add(plugin.Id);
            List<AgentMcpServer> servers = GetPluginServers(plugin);
            foreach (AgentMcpServer server in servers)
            {
                _selectedServerIds.Add(server.Id);
            }

            NotifyActivePluginSkills();
            _updateUI();
            foreach (AgentMcpServer server in servers)
            {
                await _connections.StartAsync(server, CancellationToken.None);
            }
        }

        public void DeactivateAgentPlugin(AgentPluginPackage plugin)
        {
            _selectedAgentPluginIds.Remove(plugin.Id);
            foreach (AgentMcpServer server in GetPluginServers(plugin))
            {
                _selectedServerIds.Remove(server.Id);
                _connections.Stop(server.Id);
            }

            NotifyActivePluginSkills();
        }

        public void RemoveAgentPluginPackage(AgentPluginPackage plugin)
        {
            DeactivateAgentPlugin(plugin);
            foreach (AgentMcpServer server in GetPluginServers(plugin).ToList())
            {
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

        public void NotifyActivePluginSkills()
        {
            _skillController.SetInstalledPluginSkills(_registry.Plugins.SelectMany(plugin =>
                plugin.Skills.Select(skill => new AgentPluginSkill
                {
                    Name = $"{GetAgentPluginMenuName(plugin)}/{skill.Name}",
                    Description = skill.Description,
                    SkillFilePath = skill.SkillFilePath
                })));

            if (_activePluginSkillsChanged == null)
            {
                return;
            }

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

            _activePluginSkillsChanged(activeSkills);
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
