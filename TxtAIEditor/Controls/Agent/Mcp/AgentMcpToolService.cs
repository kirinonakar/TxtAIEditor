using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpToolService
    {
        private readonly AgentMcpRegistry _registry;
        private readonly AgentMcpConnectionService _connections;
        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly ISet<string> _selectedServerIds;
        private readonly AgentMcpPluginManagerTool _pluginManagerTool;
        private readonly AgentMcpComfyUiTool _comfyUiTool;
        private readonly AgentMcpBrowserUseTool _browserUseTool;
        private readonly ISettingsService _settingsService;
        private readonly Action<LlmMessageAttachment>? _addImageAttachment;
        private readonly Dictionary<string, AgentMcpToolAlias> _toolAliases = new(StringComparer.OrdinalIgnoreCase);

        public AgentMcpToolService(
            AgentMcpRegistry registry,
            AgentMcpConnectionService connections,
            AgentMcpCredentialStore credentialStore,
            ISet<string> selectedServerIds,
            AgentMcpPluginManagerTool pluginManagerTool,
            AgentMcpComfyUiTool comfyUiTool,
            AgentMcpBrowserUseTool browserUseTool,
            ISettingsService settingsService,
            Action<LlmMessageAttachment>? addImageAttachment)
        {
            _registry = registry;
            _connections = connections;
            _credentialStore = credentialStore;
            _selectedServerIds = selectedServerIds;
            _pluginManagerTool = pluginManagerTool;
            _comfyUiTool = comfyUiTool;
            _browserUseTool = browserUseTool;
            _settingsService = settingsService;
            _addImageAttachment = addImageAttachment;
        }

        public string BuildSelectedMcpSection()
        {
            var selectedServers = GetSelectedServers();
            bool hasPluginManager = _selectedServerIds.Contains(AgentMcpPluginManagerTool.ServerId);
            bool hasComfyUi = _selectedServerIds.Contains(_comfyUiTool.ServerId);
            bool hasBrowserUse = _selectedServerIds.Contains(_browserUseTool.ServerId);
            if (selectedServers.Count == 0 && !hasComfyUi && !hasBrowserUse && !hasPluginManager)
            {
                return string.Empty;
            }

            RebuildAliases();
            var builder = new StringBuilder();
            builder.AppendLine("[Enabled MCP servers]");
            builder.AppendLine("These tools are provided by enabled MCP servers or built-in MCP-compatible plugins. Use their exact alias names in tool_call.");
            builder.AppendLine("MCP tool aliases use the form mcp_server_tool and are executed through MCP tools/call or the matching built-in plugin.");
            builder.AppendLine("Never emit a tool_call with an empty or invented name. If a tool action is intended, copy one exact enabled alias and provide all required arguments.");
            builder.AppendLine();

            if (hasPluginManager)
            {
                AppendMcpAliasSection(builder, AgentMcpPluginManagerTool.ServerName, AgentMcpPluginManagerTool.ServerId);
                builder.AppendLine("Plugin manager lists and searches installed MCPs and skills, including inactive ones. Activation changes apply before the next model request, including its tool catalog and current skill metadata. Use exact returned ids to disambiguate names.");
                builder.AppendLine();
            }

            if (hasComfyUi)
            {
                AppendMcpAliasSection(builder, _comfyUiTool.ServerName, _comfyUiTool.ServerId);
                _comfyUiTool.AppendWorkflowContext(builder);
            }

            if (hasBrowserUse)
            {
                AppendMcpAliasSection(builder, _browserUseTool.ServerName, _browserUseTool.ServerId);
                builder.AppendLine("Browser Use & Computer Use controls the installed Windows default browser and other Windows applications through OS-level keyboard and mouse input.");
                if (_settingsService.CurrentSettings.BrowserUseUiaDirectExecution)
                {
                    builder.AppendLine("UIA direct execution is enabled. focus_window selects without activating the window; status does not copy the URL. Single left clicks by ref prefer Invoke, Toggle, SelectionItem, ExpandCollapse, or LegacyIAccessible patterns. Use type_text with ref, replace=true, pressEnter=false for ValuePattern.SetValue without physical input. Unsupported patterns and coordinate clicks, other click types, typing without ref, keys, dragging, capture, and browser shortcuts use foreground input. UIA provider errors are not retried with physical input. Direct actions do not move the cursor or activate the window, but the target application itself may change focus. Always use the returned fresh snapshot; stale or ambiguous refs are rejected.");
                }
                if (_settingsService.CurrentSettings.BrowserUseCaptureEnabled)
                {
                    builder.AppendLine("Accessibility snapshots provide stable refs, and interaction tools return a fresh snapshot.");
                    builder.AppendLine("mcp_browser_use_capture is available whenever you determine that screenshot context would help with the task.");
                    builder.AppendLine("Before clicking screenshot coordinates, call mcp_browser_use_capture_target and inspect the attached marked image. Verify that the red plus is centered on the intended button or input field, not merely somewhere inside it. If it is off-center, adjust x left or right and y up or down as needed, call mcp_browser_use_capture_target again, and repeat until the marker is centered. Only then click using the same verified screenshot coordinates.");
                }
                else
                {
                    builder.AppendLine("Browser image capture is disabled in plugin settings. Use accessibility snapshots and stable refs for interactions.");
                }

                builder.AppendLine("Use read_page after navigation when selectable page text is needed.");
                if (_settingsService.CurrentSettings.BrowserUseComputerUseEnabled)
                {
                    builder.AppendLine("Computer Use is enabled. open_app returns an initial accessibility tree with stable refs. After list_windows or open_app, use focus_window when needed and choose between accessibility refs and capture based on the current task. Never guess a window id, ref, or visual coordinate.");
                }
                builder.AppendLine();
            }

            foreach (var server in selectedServers)
            {
                AppendMcpAliasSection(builder, server.Name, server.Id);
            }

            return builder.ToString().Trim();
        }

        private void AppendMcpAliasSection(StringBuilder builder, string serverName, string serverId)
        {
            builder.AppendLine($"## {serverName}");
            var aliases = _toolAliases.Values
                .Where(alias => alias.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(alias => alias.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (aliases.Count == 0)
            {
                builder.AppendLine("Status: tools not available");
                builder.AppendLine();
                return;
            }

            foreach (var alias in aliases)
            {
                builder.AppendLine($"- {alias.Alias}: {alias.Description}");
                builder.AppendLine($"  Original MCP tool: {alias.ToolName}");
                builder.AppendLine($"  Arguments JSON schema: {alias.InputSchemaJson}");
            }

            builder.AppendLine();
        }

        public bool TryGetToolAlias(string aliasName, out AgentMcpToolAlias alias)
        {
            lock (_toolAliases)
            {
                return _toolAliases.TryGetValue(aliasName, out alias!);
            }
        }

        public IReadOnlyList<AgentMcpToolAlias> GetActiveToolAliases()
        {
            lock (_toolAliases)
            {
                return _toolAliases.Values.ToList();
            }
        }

        public async Task<string> ExecuteToolAsync(string aliasName, JsonElement arguments, CancellationToken cancellationToken)
        {
            if (!TryGetToolAlias(aliasName, out var alias))
            {
                return $"MCP tool failed: unknown MCP tool alias: {aliasName}";
            }

            if (alias.IsBuiltIn)
            {
                return await ExecuteBuiltInToolAsync(alias, arguments, cancellationToken);
            }

            var server = _registry.Servers.FirstOrDefault(item => item.Id.Equals(alias.ServerId, StringComparison.OrdinalIgnoreCase));
            if (server == null)
            {
                return $"MCP tool failed: server not found for {aliasName}.";
            }

            using JsonDocument response = await _connections.ExecuteToolAsync(
                server,
                alias.ToolName,
                arguments,
                cancellationToken);

            if (AgentMcpRuntime.TryGetRpcError(response.RootElement, out string error))
            {
                return $"MCP tool failed: {_credentialStore.RedactServerSecrets(server, error)}";
            }

            if (!AgentMcpRuntime.TryGetResult(response.RootElement, out var result))
            {
                return "MCP tool returned no result.";
            }

            string formattedResult = await AgentMcpToolResultFormatter.FormatAsync(alias.Alias, result, _addImageAttachment);
            return _credentialStore.RedactServerSecrets(server, formattedResult);
        }

        private async Task<string> ExecuteBuiltInToolAsync(
            AgentMcpToolAlias alias,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (alias.ServerId.Equals(AgentMcpPluginManagerTool.ServerId, StringComparison.OrdinalIgnoreCase))
            {
                return await _pluginManagerTool.ExecuteAsync(alias.ToolName, arguments, cancellationToken);
            }

            if (_comfyUiTool.CanHandleAlias(alias))
            {
                return await _comfyUiTool.ExecuteAsync(alias, arguments, cancellationToken);
            }

            if (_browserUseTool.CanHandleAlias(alias))
            {
                return await _browserUseTool.ExecuteAsync(alias, arguments, cancellationToken);
            }

            return $"MCP tool failed: unknown built-in MCP tool alias: {alias.Alias}";
        }

        public void RebuildAliases()
        {
            lock (_toolAliases)
            {
                _toolAliases.Clear();
                var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_selectedServerIds.Contains(AgentMcpPluginManagerTool.ServerId))
                {
                    foreach (AgentMcpToolAlias alias in AgentMcpPluginManagerTool.CreateAliases())
                    {
                        usedAliases.Add(alias.Alias);
                        _toolAliases[alias.Alias] = alias;
                    }
                }
                if (_selectedServerIds.Contains(_comfyUiTool.ServerId))
                {
                    foreach (AgentMcpToolAlias alias in _comfyUiTool.CreateAliases())
                    {
                        usedAliases.Add(alias.Alias);
                        _toolAliases[alias.Alias] = alias;
                    }
                }

                if (_selectedServerIds.Contains(_browserUseTool.ServerId))
                {
                    foreach (AgentMcpToolAlias alias in _browserUseTool.CreateAliases())
                    {
                        usedAliases.Add(alias.Alias);
                        _toolAliases[alias.Alias] = alias;
                    }
                }

                foreach (var server in GetSelectedServers())
                {
                    IReadOnlyList<AgentMcpTool> tools = _connections.GetTools(server.Id);
                    if (tools.Count == 0)
                    {
                        continue;
                    }

                    foreach (var tool in tools)
                    {
                        string baseAlias = "mcp_" + SanitizeToolSegment(server.Name) + "_" + SanitizeToolSegment(tool.Name);
                        string alias = baseAlias;
                        int suffix = 2;
                        while (!usedAliases.Add(alias))
                        {
                            alias = baseAlias + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            suffix++;
                        }

                        _toolAliases[alias] = new AgentMcpToolAlias
                        {
                            Alias = alias,
                            ServerId = server.Id,
                            ServerName = server.Name,
                            ToolName = tool.Name,
                            Description = string.IsNullOrWhiteSpace(tool.Description) ? "(No description provided.)" : tool.Description,
                            InputSchemaJson = string.IsNullOrWhiteSpace(tool.InputSchemaJson) ? "{}" : tool.InputSchemaJson
                        };
                    }
                }
            }
        }

        private List<AgentMcpServer> GetSelectedServers()
        {
            return _registry.Servers
                .Where(server => _selectedServerIds.Contains(server.Id))
                .ToList();
        }

        private static string SanitizeToolSegment(string value)
        {
            string normalized = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_]+", "_").Trim('_').ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"_+", "_");
            return string.IsNullOrWhiteSpace(normalized) ? "server" : normalized;
        }
    }
}
