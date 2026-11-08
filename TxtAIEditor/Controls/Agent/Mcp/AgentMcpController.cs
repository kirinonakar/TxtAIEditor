using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpController
    {
        private readonly AgentPane _agentPane;
        private readonly ISettingsService _settingsService;
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly AgentMcpConfigurationService _configuration;
        private readonly AgentMcpDialogService _dialogService;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Action _contextChanged;
        private readonly Func<Task<IReadOnlyList<AgentMcpSkillOption>>>? _installedSkillsProvider;
        private readonly Action<string, IReadOnlyList<string>>? _builtInPluginSkillsChanged;
        private readonly AgentMcpComfyUiTool _comfyUiTool;
        private readonly AgentMcpBrowserUseTool _browserUseTool;
        private readonly AgentSkillController _skillController;
        private readonly AgentMcpPluginManagerTool _pluginManagerTool;
        private readonly AgentMcpRegistry _registry;
        private readonly AgentMcpConnectionService _connections;
        private readonly AgentMcpToolService _tools;
        private readonly AgentMcpPluginService _plugins;
        private readonly AgentMcpServerEditor _serverEditor;
        private readonly HashSet<string> _selectedServerIds = new(StringComparer.OrdinalIgnoreCase);
        private string _comfyUiStatus = string.Empty;

        public AgentMcpController(
            AgentPane agentPane,
            Action<object> initializePickerWindow,
            ISettingsService settingsService,
            ICredentialService credentialService,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Action contextChanged,
            Func<string> workspaceRootProvider,
            Func<string, Task>? fileModifiedAsync,
            Action? beforeDialog,
            Action? afterDialog,
            AgentSkillController skillController,
            Action<LlmMessageAttachment>? addImageAttachment = null,
            Action<IReadOnlyList<AgentPluginSkill>>? activePluginSkillsChanged = null,
            Func<Task<IReadOnlyList<AgentMcpSkillOption>>>? installedSkillsProvider = null,
            Action<string, IReadOnlyList<string>>? builtInPluginSkillsChanged = null)
        {
            _agentPane = agentPane;
            _settingsService = settingsService;
            _showError = showError;
            _getString = getString;
            _contextChanged = contextChanged;
            _installedSkillsProvider = installedSkillsProvider;
            _builtInPluginSkillsChanged = builtInPluginSkillsChanged;
            _skillController = skillController;

            _credentialStore = new AgentMcpCredentialStore(credentialService);
            _configuration = new AgentMcpConfigurationService(_credentialStore, getString);
            _registry = new AgentMcpRegistry(_credentialStore);
            var oauthService = new AgentMcpOAuthService(_credentialStore, getString, SaveAsync);
            var runtime = new AgentMcpRuntime(_credentialStore, oauthService, workspaceRootProvider, getString);
            _connections = new AgentMcpConnectionService(runtime, _selectedServerIds.Contains, getString);
            _dialogService = new AgentMcpDialogService(agentPane, initializePickerWindow, getString, beforeDialog, afterDialog);
            _comfyUiTool = new AgentMcpComfyUiTool(workspaceRootProvider, () => settingsService.CurrentSettings, fileModifiedAsync);
            _browserUseTool = new AgentMcpBrowserUseTool(() => settingsService.CurrentSettings, addImageAttachment);
            _pluginManagerTool = new AgentMcpPluginManagerTool(GetPluginManagerEntriesAsync, SetPluginActive);
            _tools = new AgentMcpToolService(
                _registry, _connections, _credentialStore, _selectedServerIds,
                _pluginManagerTool, _comfyUiTool, _browserUseTool, settingsService, addImageAttachment);
            _serverEditor = new AgentMcpServerEditor(
                _registry, _connections, _credentialStore, _configuration, oauthService, _dialogService,
                _selectedServerIds, initializePickerWindow, showError, getString, SaveAsync);
            _plugins = new AgentMcpPluginService(
                _registry, _connections, _credentialStore, _selectedServerIds, _dialogService,
                initializePickerWindow, showError, getString, SaveAsync, RebuildAliases, UpdateUI,
                skillController, activePluginSkillsChanged);
            _connections.Changed += OnConnectionChanged;
        }

        private void OnConnectionChanged()
        {
            RebuildAliases();
            UpdateUI();
        }

        public async Task LoadAsync()
        {
            bool migrated = await _registry.LoadAsync();
            _selectedServerIds.RemoveWhere(id =>
                !id.Equals(AgentMcpPluginManagerTool.ServerId, StringComparison.OrdinalIgnoreCase) &&
                !_comfyUiTool.IsServerId(id) &&
                !_browserUseTool.IsServerId(id) &&
                _registry.Servers.All(server => !server.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
            _plugins.PruneSelection();
            _plugins.NotifyActivePluginSkills();
            UpdateComfyUiRelatedSkills();
            if (migrated)
            {
                await SaveAsync();
                return;
            }

            RebuildAliases();
            UpdateUI();
        }

        public void Close() => _connections.Dispose();

        public Task AddMcpAsync() => _serverEditor.AddMcpAsync();

        public Task ExportMcpAsync() => _serverEditor.ExportMcpAsync();

        public Task ImportMcpAsync() => _serverEditor.ImportMcpAsync();

        public Task ImportAgentPluginAsync() => _plugins.ImportAgentPluginAsync();

        public Task InstallAgentPluginFromGitHubAsync() => _plugins.InstallAgentPluginFromGitHubAsync();

        public Task EditMcpAsync(string serverName)
        {
            if (_comfyUiTool.IsServerName(serverName) || _plugins.FindAgentPluginByMenuName(serverName) != null)
            {
                return Task.CompletedTask;
            }

            AgentMcpServer? server = _registry.FindServer(serverName);
            return server == null ? Task.CompletedTask : _serverEditor.EditMcpAsync(server);
        }

        public async Task ConfigureBuiltInMcpAsync(string serverName)
        {
            if (_browserUseTool.IsServerName(serverName))
            {
                var browserSettings = _settingsService.CurrentSettings;
                var browserInput = await _dialogService.ShowBrowserUseSettingsAsync(new AgentMcpBrowserUseSettingsInput
                {
                    AllowInteraction = browserSettings.BrowserUseAllowInteraction,
                    CaptureEnabled = browserSettings.BrowserUseCaptureEnabled,
                    ComputerUseEnabled = browserSettings.BrowserUseComputerUseEnabled,
                    UiaDirectExecution = browserSettings.BrowserUseUiaDirectExecution
                });
                if (browserInput == null)
                {
                    return;
                }

                browserSettings.BrowserUseAllowInteraction = browserInput.AllowInteraction;
                browserSettings.BrowserUseCaptureEnabled = browserInput.CaptureEnabled;
                browserSettings.BrowserUseComputerUseEnabled = browserInput.ComputerUseEnabled;
                browserSettings.BrowserUseUiaDirectExecution = browserInput.UiaDirectExecution;
                await _settingsService.SaveSettingsAsync(browserSettings);
                RebuildAliases();
                UpdateUI();
                return;
            }

            if (!_comfyUiTool.IsServerName(serverName))
            {
                return;
            }

            var settings = _settingsService.CurrentSettings;
            string initialWorkflowDirectory = string.IsNullOrWhiteSpace(settings.ComfyUiWorkflowDirectory)
                ? EditorSettings.GetDefaultComfyUiWorkflowDirectory()
                : settings.ComfyUiWorkflowDirectory;
            IReadOnlyList<AgentMcpSkillOption> installedSkills = _installedSkillsProvider == null
                ? Array.Empty<AgentMcpSkillOption>()
                : await _installedSkillsProvider();
            var input = await _dialogService.ShowComfyUiSettingsAsync(
                new AgentMcpComfyUiSettingsInput
                {
                    LaunchPath = settings.ComfyUiLaunchPath,
                    WorkflowDirectory = initialWorkflowDirectory,
                    RelatedSkillNames = settings.ComfyUiRelatedSkillNames?.ToList() ?? new List<string>()
                },
                installedSkills);
            if (input == null)
            {
                return;
            }

            if (!_configuration.TryNormalizeOptionalExistingFilePath(input.LaunchPath, out string launchPath, out string launchError))
            {
                _showError(
                    _getString("AgentMcpComfyUiSettingsTitle", "ComfyUI 설정"),
                    launchError);
                return;
            }

            if (!_configuration.TryNormalizeDirectoryPath(input.WorkflowDirectory, out string workflowDirectory, out string workflowError))
            {
                _showError(
                    _getString("AgentMcpComfyUiSettingsTitle", "ComfyUI 설정"),
                    workflowError);
                return;
            }

            settings.ComfyUiLaunchPath = launchPath;
            settings.ComfyUiWorkflowDirectory = workflowDirectory;
            settings.ComfyUiRelatedSkillNames = input.RelatedSkillNames?.ToList() ?? new List<string>();
            await _settingsService.SaveSettingsAsync(settings);
            UpdateComfyUiRelatedSkills();
            RebuildAliases();
            UpdateUI();
        }

        public async Task ToggleMcpAsync(string serverName)
        {
            if (AgentMcpPluginManagerTool.IsServerName(serverName))
            {
                if (!_selectedServerIds.Add(AgentMcpPluginManagerTool.ServerId))
                {
                    _selectedServerIds.Remove(AgentMcpPluginManagerTool.ServerId);
                }
                RebuildAliases();
                UpdateUI();
                return;
            }

            if (_browserUseTool.IsServerName(serverName))
            {
                if (!_selectedServerIds.Add(_browserUseTool.ServerId))
                {
                    _selectedServerIds.Remove(_browserUseTool.ServerId);
                }

                RebuildAliases();
                UpdateUI();
                return;
            }

            if (_comfyUiTool.IsServerName(serverName))
            {
                if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
                {
                    _selectedServerIds.Remove(_comfyUiTool.ServerId);
                    _comfyUiStatus = string.Empty;
                    UpdateComfyUiRelatedSkills();
                    RebuildAliases();
                    UpdateUI();
                    return;
                }

                _selectedServerIds.Add(_comfyUiTool.ServerId);
                UpdateComfyUiRelatedSkills();
                RebuildAliases();
                UpdateUI();
                await EnsureBuiltInComfyUiReadyAsync(CancellationToken.None);
                return;
            }

            AgentPluginPackage? plugin = _plugins.FindAgentPluginByMenuName(serverName);
            if (plugin != null)
            {
                await _plugins.ToggleAgentPluginAsync(plugin);
                return;
            }

            var server = _registry.FindServer(serverName);
            if (server == null)
            {
                return;
            }

            if (!_selectedServerIds.Add(server.Id))
            {
                _selectedServerIds.Remove(server.Id);
                _connections.Stop(server.Id);
                RebuildAliases();
                UpdateUI();
                return;
            }

            UpdateUI();
            await _connections.StartAsync(server, CancellationToken.None);
        }

        public async Task DeleteMcpAsync(string serverName)
        {
            if (AgentMcpPluginManagerTool.IsServerName(serverName) ||
                _comfyUiTool.IsServerName(serverName) || _browserUseTool.IsServerName(serverName))
            {
                return;
            }

            AgentPluginPackage? plugin = _plugins.FindAgentPluginByMenuName(serverName);
            if (plugin != null)
            {
                _plugins.RemoveAgentPluginPackage(plugin);
                await SaveAsync();
                return;
            }

            var server = _registry.FindServer(serverName);
            if (server == null)
            {
                return;
            }

            _credentialStore.DeleteAllCredentials(server);
            _registry.Servers.Remove(server);
            _selectedServerIds.Remove(server.Id);
            _connections.Stop(server.Id);
            await SaveAsync();
        }

        public void RemoveSelectedMcp(string serverName)
        {
            if (AgentMcpPluginManagerTool.IsServerName(serverName))
            {
                _selectedServerIds.Remove(AgentMcpPluginManagerTool.ServerId);
                RebuildAliases();
                UpdateUI();
                return;
            }

            if (_browserUseTool.IsServerName(serverName))
            {
                _selectedServerIds.Remove(_browserUseTool.ServerId);
                RebuildAliases();
                UpdateUI();
                return;
            }

            if (_comfyUiTool.IsServerName(serverName))
            {
                _selectedServerIds.Remove(_comfyUiTool.ServerId);
                UpdateComfyUiRelatedSkills();
                RebuildAliases();
                UpdateUI();
                return;
            }

            AgentPluginPackage? plugin = _plugins.FindAgentPluginByMenuName(serverName);
            if (plugin != null)
            {
                _plugins.DeactivateAgentPlugin(plugin);
                RebuildAliases();
                UpdateUI();
                return;
            }

            var server = _registry.FindServer(serverName);
            if (server == null)
            {
                return;
            }

            _selectedServerIds.Remove(server.Id);
            _connections.Stop(server.Id);
            RebuildAliases();
            UpdateUI();
        }

        public void ClearSelectedMcps()
        {
            _plugins.ClearSelection();

            foreach (AgentMcpServer server in GetSelectedServers().ToList())
            {
                _connections.Stop(server.Id);
            }

            _selectedServerIds.Clear();
            UpdateComfyUiRelatedSkills();
            RebuildAliases();
            UpdateUI();
        }

        public string GetSelectedMcpLabel()
        {
            var labels = new List<string>();
            if (_selectedServerIds.Contains(AgentMcpPluginManagerTool.ServerId))
            {
                labels.Add(AgentMcpPluginManagerTool.ServerName);
            }
            if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
            {
                labels.Add(_comfyUiTool.ServerName);
            }

            if (_selectedServerIds.Contains(_browserUseTool.ServerId))
            {
                labels.Add(_browserUseTool.ServerName);
            }

            labels.AddRange(_registry.Plugins
                .Where(_plugins.IsPluginMcpSelectionComplete)
                .Select(AgentMcpPluginService.GetAgentPluginMenuName));
            labels.AddRange(GetSelectedServers()
                .Where(_plugins.ShowIndividualServerChip)
                .Select(server => server.Name));
            return string.Join(", ", labels);
        }

        public bool HasSelectedMcpServers()
        {
            return _selectedServerIds.Contains(AgentMcpPluginManagerTool.ServerId) ||
                _selectedServerIds.Contains(_comfyUiTool.ServerId) ||
                _selectedServerIds.Contains(_browserUseTool.ServerId) ||
                _plugins.HasSelectedPlugins ||
                GetSelectedServers().Count > 0;
        }

        private async Task<IReadOnlyList<AgentPluginEntry>> GetPluginManagerEntriesAsync()
        {
            var entries = new List<AgentPluginEntry>
            {
                new("mcp:" + AgentMcpPluginManagerTool.ServerId, "mcp", AgentMcpPluginManagerTool.ServerName,
                    "List, search, activate and deactivate installed MCPs and skills.",
                    _selectedServerIds.Contains(AgentMcpPluginManagerTool.ServerId)),
                new("mcp:" + _comfyUiTool.ServerId, "mcp", _comfyUiTool.ServerName,
                    "Generate images using ComfyUI workflows.", _selectedServerIds.Contains(_comfyUiTool.ServerId)),
                new("mcp:" + _browserUseTool.ServerId, "mcp", _browserUseTool.ServerName,
                    "Browser and Windows application tools.", _selectedServerIds.Contains(_browserUseTool.ServerId))
            };
            entries.AddRange(_registry.Servers.Select(server => new AgentPluginEntry(
                "mcp:" + server.Id, "mcp", server.Name, server.AgentPluginName,
                _selectedServerIds.Contains(server.Id))));
            entries.AddRange(await _skillController.GetPluginManagerEntriesAsync());
            return entries;
        }

        private void SetPluginActive(AgentPluginEntry entry, bool active)
        {
            if (entry.Type == "skill")
            {
                _skillController.SetSkillActive(entry.Name, active);
                return;
            }

            string serverId = entry.Id.Substring("mcp:".Length);
            bool changed = active ? _selectedServerIds.Add(serverId) : _selectedServerIds.Remove(serverId);
            if (!changed)
            {
                return;
            }
            if (!active && _registry.Servers.Any(server => server.Id.Equals(serverId, StringComparison.OrdinalIgnoreCase)))
            {
                _connections.Stop(serverId);
            }
            if (_comfyUiTool.IsServerId(serverId))
            {
                UpdateComfyUiRelatedSkills();
            }
        }

        public async Task ApplyPendingPluginChangesAsync(CancellationToken cancellationToken)
        {
            if (!_pluginManagerTool.ApplyPendingChanges(cancellationToken))
            {
                return;
            }
            RebuildAliases();
            UpdateUI();
            await EnsureActiveToolsAsync(cancellationToken);
        }

        public async Task EnsureActiveToolsAsync(CancellationToken cancellationToken)
        {
            RebuildAliases();
            if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
            {
                await EnsureBuiltInComfyUiReadyAsync(cancellationToken);
            }

            foreach (var server in GetSelectedServers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_connections.GetTools(server.Id).Count == 0)
                {
                    using var readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readinessCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await _connections.StartAsync(server, readinessCancellation.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Keep the shared MCP start running, but do not block the Agent prompt indefinitely.
                    }
                }
            }
        }

        public string BuildSelectedMcpSection() => _tools.BuildSelectedMcpSection();

        public bool TryGetToolAlias(string aliasName, out AgentMcpToolAlias alias)
            => _tools.TryGetToolAlias(aliasName, out alias);

        public IReadOnlyList<AgentMcpToolAlias> GetActiveToolAliases() => _tools.GetActiveToolAliases();

        public Task<string> ExecuteToolAsync(string aliasName, JsonElement arguments, CancellationToken cancellationToken)
            => _tools.ExecuteToolAsync(aliasName, arguments, cancellationToken);

        private void RebuildAliases() => _tools.RebuildAliases();

        private void UpdateUI()
        {
            var items = new List<AgentMcpItem>
            {
                AgentMcpPluginManagerTool.CreateMenuItem(_selectedServerIds.Contains(AgentMcpPluginManagerTool.ServerId), _getString),
                _comfyUiTool.CreateMenuItem(_selectedServerIds.Contains(_comfyUiTool.ServerId), _getString, _comfyUiStatus),
                _browserUseTool.CreateMenuItem(_selectedServerIds.Contains(_browserUseTool.ServerId), _getString)
            };

            items.AddRange(_registry.Plugins
                .OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(plugin =>
                {
                    List<AgentMcpServer> pluginServers = _plugins.GetPluginServers(plugin);
                    string componentSummary = string.Format(
                        _getString("AgentPluginComponentSummaryFormat", "MCP {0}개 · Skill {1}개"),
                        pluginServers.Count,
                        plugin.Skills.Count);
                    string[] statusMessages = pluginServers
                        .Select(server => _connections.GetStatus(server.Id))
                        .Where(status => !string.IsNullOrWhiteSpace(status))
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                    string detail = statusMessages.Length == 0
                        ? componentSummary
                        : $"{componentSummary} - {string.Join(", ", statusMessages)}";
                    return new AgentMcpItem
                    {
                        Name = AgentMcpPluginService.GetAgentPluginMenuName(plugin),
                        Endpoint = componentSummary,
                        Detail = detail,
                        IsSelected = _plugins.IsPluginMcpSelectionComplete(plugin),
                        IsAgentPlugin = true,
                        CanEdit = false
                    };
                }));

            items.AddRange(_registry.Servers
                .Where(server => !server.IsAgentPlugin || _plugins.ShowIndividualServerChip(server))
                .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(server =>
                {
                    bool isMemoryServer = AgentMcpRuntime.IsMemoryServer(server);
                    IEnumerable<string> displayArguments = isMemoryServer
                        ? server.Arguments
                        : server.Arguments.Append(server.TargetDirectory);
                    string connectionArguments = string.Join(" ", displayArguments
                        .Where(argument => !string.IsNullOrWhiteSpace(argument)));
                    string connectionDetail = AgentMcpTransportTypes.IsStdio(server.Transport)
                        ? $"stdio: {server.Command} {connectionArguments}".TrimEnd()
                        : server.Endpoint;
                    if (isMemoryServer && !string.IsNullOrWhiteSpace(server.TargetDirectory))
                    {
                        connectionDetail += $" - memory: {Path.Combine(server.TargetDirectory, "memory.jsonl")}";
                    }
                    string detail = !string.IsNullOrWhiteSpace(_connections.GetStatus(server.Id))
                        ? $"{connectionDetail} - {_connections.GetStatus(server.Id)}"
                        : connectionDetail;
                    if (server.Stateless && !AgentMcpTransportTypes.IsStdio(server.Transport))
                    {
                        detail += $" - {_getString("AgentMcpStatelessSummary", "stateless HTTP")}";
                    }
                    if (!AgentMcpTransportTypes.IsStdio(server.Transport) && server.Headers.Count > 0)
                    {
                        detail += $" - headers: {string.Join(", ", server.Headers.Keys)}";
                    }

                    return new AgentMcpItem
                    {
                        Name = server.Name,
                        Endpoint = connectionDetail,
                        Detail = detail,
                        IsSelected = _selectedServerIds.Contains(server.Id),
                        CanEdit = !server.IsAgentPlugin,
                        CanDelete = !server.IsAgentPlugin,
                        CanConfigure = !server.IsAgentPlugin
                    };
                })
                .ToList());
            var selectedNames = new List<string>();
            if (_selectedServerIds.Contains(AgentMcpPluginManagerTool.ServerId))
            {
                selectedNames.Add(AgentMcpPluginManagerTool.ServerName);
            }
            if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
            {
                selectedNames.Add(_comfyUiTool.ServerName);
            }

            if (_selectedServerIds.Contains(_browserUseTool.ServerId))
            {
                selectedNames.Add(_browserUseTool.ServerName);
            }

            selectedNames.AddRange(_registry.Plugins
                .Where(_plugins.IsPluginMcpSelectionComplete)
                .Select(AgentMcpPluginService.GetAgentPluginMenuName));
            selectedNames.AddRange(GetSelectedServers()
                .Where(_plugins.ShowIndividualServerChip)
                .Select(server => server.Name));

            void ApplyUI()
            {
                _agentPane.UpdateAgentMcpMenu(items, selectedNames, _getString);
                QueueContextChanged();
            }

            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.HasThreadAccess == true)
            {
                ApplyUI();
                return;
            }

            if (dispatcher?.TryEnqueue(ApplyUI) == true)
            {
                return;
            }

            ApplyUI();
        }

        private void QueueContextChanged()
        {
            var dispatcher = _agentPane.DispatcherQueue;
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _contextChanged()) == true)
            {
                return;
            }

            _contextChanged();
        }

        private async Task EnsureBuiltInComfyUiReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await _comfyUiTool.IsServerRunningAsync(cancellationToken))
                {
                    _comfyUiStatus = _getString("AgentMcpComfyUiStartStatusRunning", "실행 중");
                    UpdateUI();
                    return;
                }

                if (!_comfyUiTool.TryStartConfiguredComfyUi(out ComfyUiLaunchFailure failure, out string detail))
                {
                    _comfyUiStatus = failure switch
                    {
                        ComfyUiLaunchFailure.MissingPath => _getString("AgentMcpComfyUiStartStatusLaunchPathMissing", "실행 파일 경로 필요"),
                        ComfyUiLaunchFailure.FileNotFound => string.Format(
                            _getString("AgentMcpComfyUiStartStatusLaunchPathNotFound", "실행 파일 없음: {0}"),
                            detail),
                        _ => string.Format(
                            _getString("AgentMcpComfyUiStartStatusFailedFormat", "실행 실패: {0}"),
                            detail)
                    };
                    UpdateUI();
                    return;
                }

                _comfyUiStatus = _getString("AgentMcpComfyUiStartStatusStarting", "실행 시작 중");
                UpdateUI();
                bool becameReady = await _comfyUiTool.WaitForServerRunningAsync(TimeSpan.FromSeconds(12), cancellationToken);
                _comfyUiStatus = becameReady
                    ? _getString("AgentMcpComfyUiStartStatusRunning", "실행 중")
                    : _getString("AgentMcpComfyUiStartStatusStarted", "실행 시작됨");
                UpdateUI();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _comfyUiStatus = string.Format(
                    _getString("AgentMcpComfyUiStartStatusFailedFormat", "실행 실패: {0}"),
                    ex.Message);
                UpdateUI();
            }
        }

        private async Task SaveAsync()
        {
            await _registry.SaveAsync();
            RebuildAliases();
            UpdateUI();
        }

        private void UpdateComfyUiRelatedSkills()
        {
            if (_builtInPluginSkillsChanged == null)
            {
                return;
            }

            IReadOnlyList<string> relatedSkillNames = _selectedServerIds.Contains(_comfyUiTool.ServerId)
                ? _settingsService.CurrentSettings.ComfyUiRelatedSkillNames ?? new List<string>()
                : Array.Empty<string>();
            _builtInPluginSkillsChanged(_comfyUiTool.ServerId, relatedSkillNames);
        }

        private List<AgentMcpServer> GetSelectedServers()
        {
            return _registry.Servers
                .Where(server => _selectedServerIds.Contains(server.Id))
                .ToList();
        }
    }
}
