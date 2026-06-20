using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpServer
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class AgentMcpItem
    {
        public string Name { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public bool IsSelected { get; set; }
    }

    internal sealed class AgentMcpToolAlias
    {
        public string Alias { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string InputSchemaJson { get; set; } = "{}";
    }

    internal sealed class AgentMcpController
    {
        private const string ProtocolVersion = "2025-06-18";
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly AgentPane _agentPane;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Action _contextChanged;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly string _mcpFilePath;
        private readonly List<AgentMcpServer> _servers = new();
        private readonly HashSet<string> _selectedServerIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AgentMcpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AgentMcpToolAlias> _toolAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _serverStatus = new(StringComparer.OrdinalIgnoreCase);

        public AgentMcpController(
            AgentPane agentPane,
            Action<object> initializePickerWindow,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Action contextChanged,
            Action? beforeDialog,
            Action? afterDialog)
        {
            _agentPane = agentPane;
            _initializePickerWindow = initializePickerWindow;
            _showError = showError;
            _getString = getString;
            _contextChanged = contextChanged;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsDir = Path.Combine(userProfile, ".TxtAIEditor");
            _mcpFilePath = Path.Combine(settingsDir, "agent-mcp-servers.json");
        }

        public async Task LoadAsync()
        {
            try
            {
                if (File.Exists(_mcpFilePath))
                {
                    string json = await File.ReadAllTextAsync(_mcpFilePath);
                    var loaded = JsonSerializer.Deserialize<List<AgentMcpServer>>(json);
                    if (loaded != null)
                    {
                        _servers.Clear();
                        _servers.AddRange(loaded
                            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Endpoint))
                            .Select(s =>
                            {
                                if (string.IsNullOrWhiteSpace(s.Id))
                                {
                                    s.Id = Guid.NewGuid().ToString("N");
                                }

                                s.Name = s.Name.Trim();
                                s.Endpoint = s.Endpoint.Trim();
                                s.Headers = NormalizeHeaders(s.Headers);
                                return s;
                            }));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load MCP servers: {ex.Message}");
            }

            _selectedServerIds.RemoveWhere(id => _servers.All(server => !server.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
            RebuildAliases();
            UpdateUI();
        }

        public async Task AddMcpAsync()
        {
            var nameBox = CreateTextBox(_getString("AgentMcpNamePlaceholder", "MCP 이름 입력..."));
            var endpointBox = CreateTextBox(_getString("AgentMcpEndpointPlaceholder", "https://server.example/mcp"));
            var headerNameBox = CreateTextBox(_getString("AgentMcpHeaderNamePlaceholder", "CONTEXT7_API_KEY"));
            var apiKeyBox = CreatePasswordBox(_getString("AgentMcpApiKeyPlaceholder", "API Key 입력..."));
            var stack = new StackPanel { Spacing = 10, Width = 420 };
            stack.Children.Add(new TextBlock { Text = _getString("AgentMcpNameLabel", "MCP 이름") });
            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock { Text = _getString("AgentMcpEndpointLabel", "MCP 주소") });
            stack.Children.Add(endpointBox);
            stack.Children.Add(new TextBlock { Text = _getString("AgentMcpHeaderNameLabel", "API Key Header 이름") });
            stack.Children.Add(headerNameBox);
            stack.Children.Add(new TextBlock { Text = _getString("AgentMcpApiKeyLabel", "API Key") });
            stack.Children.Add(apiKeyBox);

            var dialog = new ContentDialog
            {
                Title = _getString("AgentMcpAddText", "MCP 추가"),
                Content = stack,
                PrimaryButtonText = _getString("AgentMcpSaveAddButton", "추가"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string name = nameBox.Text?.Trim() ?? string.Empty;
            string endpoint = endpointBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    _getString("AgentMcpNameEndpointRequired", "MCP 이름과 주소를 입력해주세요."));
                return;
            }

            if (!IsValidHttpEndpoint(endpoint))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    _getString("AgentMcpEndpointInvalid", "MCP 주소는 http 또는 https URL이어야 합니다."));
                return;
            }

            if (!TryBuildHeaders(headerNameBox.Text, apiKeyBox.Password, out var headers))
            {
                _showError(
                    _getString("AgentMcpAddErrorTitle", "MCP 추가 오류"),
                    _getString("AgentMcpHeaderPairRequired", "API Key를 입력하려면 Header 이름도 함께 입력해주세요."));
                return;
            }

            var existing = _servers.FirstOrDefault(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Endpoint = endpoint;
                existing.Headers = headers;
                _sessions.Remove(existing.Id);
                _serverStatus.Remove(existing.Id);
            }
            else
            {
                _servers.Add(new AgentMcpServer
                {
                    Name = name,
                    Endpoint = endpoint,
                    Headers = headers
                });
            }

            await SaveAsync();
        }

        public async Task ExportMcpAsync()
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "agent-mcp-servers.json"
            };
            _initializePickerWindow(picker);
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string json = JsonSerializer.Serialize(_servers, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(file.Path, json);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentMcpExportErrorTitle", "MCP 내보내기 오류"),
                    string.Format(_getString("AgentMcpExportErrorMessage", "MCP 목록을 내보내는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        public async Task EditMcpAsync(string serverName)
        {
            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            var nameBox = CreateTextBox(_getString("AgentMcpNamePlaceholder", "MCP 이름 입력..."));
            nameBox.Text = server.Name;
            var endpointBox = CreateTextBox(_getString("AgentMcpEndpointPlaceholder", "https://server.example/mcp"));
            endpointBox.Text = server.Endpoint;
            var existingHeader = server.Headers.FirstOrDefault();
            var headerNameBox = CreateTextBox(_getString("AgentMcpHeaderNamePlaceholder", "CONTEXT7_API_KEY"));
            headerNameBox.Text = existingHeader.Key ?? string.Empty;
            var apiKeyBox = CreatePasswordBox(_getString("AgentMcpApiKeyPlaceholder", "API Key 입력..."));
            apiKeyBox.Password = existingHeader.Value ?? string.Empty;

            var stack = new StackPanel { Spacing = 10, Width = 420 };
            stack.Children.Add(new TextBlock { Text = _getString("AgentMcpNameLabel", "MCP 이름") });
            stack.Children.Add(nameBox);
            stack.Children.Add(new TextBlock { Text = _getString("AgentMcpEndpointLabel", "MCP 주소") });
            stack.Children.Add(endpointBox);
            stack.Children.Add(new TextBlock { Text = _getString("AgentMcpHeaderNameLabel", "API Key Header 이름") });
            stack.Children.Add(headerNameBox);
            stack.Children.Add(new TextBlock { Text = _getString("AgentMcpApiKeyLabel", "API Key") });
            stack.Children.Add(apiKeyBox);

            var dialog = new ContentDialog
            {
                Title = _getString("AgentMcpEditTitle", "MCP 수정"),
                Content = stack,
                PrimaryButtonText = _getString("AgentMcpEditSaveButton", "저장"),
                CloseButtonText = _getString("AgentPresetSaveCancelButton", "취소"),
                DefaultButton = ContentDialogButton.None,
                XamlRoot = _agentPane.XamlRoot,
                RequestedTheme = _agentPane.ActualTheme
            };

            var result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string name = nameBox.Text?.Trim() ?? string.Empty;
            string endpoint = endpointBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    _getString("AgentMcpNameEndpointRequired", "MCP 이름과 주소를 입력해주세요."));
                return;
            }

            if (!IsValidHttpEndpoint(endpoint))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    _getString("AgentMcpEndpointInvalid", "MCP 주소는 http 또는 https URL이어야 합니다."));
                return;
            }

            if (!TryBuildHeaders(headerNameBox.Text, apiKeyBox.Password, out var headers))
            {
                _showError(
                    _getString("AgentMcpEditErrorTitle", "MCP 수정 오류"),
                    _getString("AgentMcpHeaderPairRequired", "API Key를 입력하려면 Header 이름도 함께 입력해주세요."));
                return;
            }

            var duplicate = _servers.FirstOrDefault(item =>
                !item.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                _servers.Remove(duplicate);
                _selectedServerIds.Remove(duplicate.Id);
                _sessions.Remove(duplicate.Id);
                _serverStatus.Remove(duplicate.Id);
            }

            server.Name = name;
            server.Endpoint = endpoint;
            server.Headers = headers;
            _sessions.Remove(server.Id);
            _serverStatus.Remove(server.Id);
            await SaveAsync();
        }

        public async Task ImportMcpAsync()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            _initializePickerWindow(picker);
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(file.Path);
                var imported = DeserializeImportedServers(json);
                if (imported.Count == 0)
                {
                    throw new InvalidDataException(_getString("AgentMcpImportInvalidFile", "가져올 수 있는 MCP JSON이 아닙니다."));
                }

                foreach (var item in imported)
                {
                    string name = item.Name?.Trim() ?? string.Empty;
                    string endpoint = item.Endpoint?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.IsNullOrWhiteSpace(endpoint) ||
                        !IsValidHttpEndpoint(endpoint))
                    {
                        continue;
                    }

                    var existing = FindServer(name);
                    if (existing != null)
                    {
                        existing.Endpoint = endpoint;
                        existing.Headers = NormalizeHeaders(item.Headers);
                        _sessions.Remove(existing.Id);
                        _serverStatus.Remove(existing.Id);
                    }
                    else
                    {
                        _servers.Add(new AgentMcpServer
                        {
                            Name = name,
                            Endpoint = endpoint,
                            Headers = NormalizeHeaders(item.Headers)
                        });
                    }
                }

                await SaveAsync();
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentMcpImportErrorTitle", "MCP 가져오기 오류"),
                    string.Format(_getString("AgentMcpImportErrorMessage", "MCP 목록을 가져오는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        public async Task ToggleMcpAsync(string serverName)
        {
            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            if (!_selectedServerIds.Add(server.Id))
            {
                _selectedServerIds.Remove(server.Id);
                _sessions.Remove(server.Id);
                _serverStatus.Remove(server.Id);
                RebuildAliases();
                UpdateUI();
                return;
            }

            UpdateUI();
            await RefreshServerToolsAsync(server, CancellationToken.None);
        }

        public async Task DeleteMcpAsync(string serverName)
        {
            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            _servers.Remove(server);
            _selectedServerIds.Remove(server.Id);
            _sessions.Remove(server.Id);
            _serverStatus.Remove(server.Id);
            await SaveAsync();
        }

        public void RemoveSelectedMcp(string serverName)
        {
            var server = FindServer(serverName);
            if (server == null)
            {
                return;
            }

            _selectedServerIds.Remove(server.Id);
            _sessions.Remove(server.Id);
            _serverStatus.Remove(server.Id);
            RebuildAliases();
            UpdateUI();
        }

        public string GetSelectedMcpLabel()
        {
            return string.Join(", ", GetSelectedServers().Select(server => server.Name));
        }

        public async Task EnsureActiveToolsAsync(CancellationToken cancellationToken)
        {
            foreach (var server in GetSelectedServers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_sessions.TryGetValue(server.Id, out var session) || session.Tools.Count == 0)
                {
                    await RefreshServerToolsAsync(server, cancellationToken);
                }
            }
        }

        public string BuildSelectedMcpSection()
        {
            var selectedServers = GetSelectedServers();
            if (selectedServers.Count == 0)
            {
                return string.Empty;
            }

            RebuildAliases();
            var builder = new StringBuilder();
            builder.AppendLine("[Enabled MCP servers]");
            builder.AppendLine("These external tools are provided by Model Context Protocol servers. Use their exact alias names in tool_call.");
            builder.AppendLine("MCP tool aliases use the form mcp_server_tool and are executed through MCP tools/call.");
            builder.AppendLine();

            foreach (var server in selectedServers)
            {
                builder.AppendLine($"## {server.Name}");
                builder.AppendLine($"Endpoint: {server.Endpoint}");
                var aliases = _toolAliases.Values
                    .Where(alias => alias.ServerId.Equals(server.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(alias => alias.Alias, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (aliases.Count == 0)
                {
                    string status = _serverStatus.TryGetValue(server.Id, out string? value) ? value : "tools not loaded yet";
                    builder.AppendLine($"Status: {status}");
                    builder.AppendLine();
                    continue;
                }

                foreach (var alias in aliases)
                {
                    builder.AppendLine($"- {alias.Alias}: {alias.Description}");
                    builder.AppendLine($"  Original MCP tool: {alias.ToolName}");
                    builder.AppendLine($"  Arguments JSON schema: {alias.InputSchemaJson}");
                }

                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }

        public bool TryGetToolAlias(string aliasName, out AgentMcpToolAlias alias)
        {
            return _toolAliases.TryGetValue(aliasName, out alias!);
        }

        public async Task<string> ExecuteToolAsync(string aliasName, JsonElement arguments, CancellationToken cancellationToken)
        {
            if (!TryGetToolAlias(aliasName, out var alias))
            {
                return $"MCP tool failed: unknown MCP tool alias: {aliasName}";
            }

            var server = _servers.FirstOrDefault(item => item.Id.Equals(alias.ServerId, StringComparison.OrdinalIgnoreCase));
            if (server == null)
            {
                return $"MCP tool failed: server not found for {aliasName}.";
            }

            AgentMcpSession session = await EnsureSessionAsync(server, cancellationToken);
            JsonElement callArguments = arguments.ValueKind == JsonValueKind.Object
                ? arguments.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            using JsonDocument response = await SendJsonRpcAsync(
                server,
                session,
                "tools/call",
                new Dictionary<string, object?>
                {
                    ["name"] = alias.ToolName,
                    ["arguments"] = callArguments
                },
                cancellationToken);

            if (TryGetRpcError(response.RootElement, out string error))
            {
                return $"MCP tool failed: {error}";
            }

            if (!TryGetResult(response.RootElement, out var result))
            {
                return "MCP tool returned no result.";
            }

            return FormatToolCallResult(alias, result);
        }

        private async Task RefreshServerToolsAsync(AgentMcpServer server, CancellationToken cancellationToken)
        {
            try
            {
                _serverStatus[server.Id] = _getString("AgentMcpStatusConnecting", "연결 중");
                UpdateUI();
                AgentMcpSession session = await EnsureSessionAsync(server, cancellationToken, forceRefresh: true);
                session.Tools.Clear();

                string? cursor = null;
                do
                {
                    Dictionary<string, object?> parameters = new();
                    if (!string.IsNullOrEmpty(cursor))
                    {
                        parameters["cursor"] = cursor;
                    }

                    using JsonDocument response = await SendJsonRpcAsync(server, session, "tools/list", parameters, cancellationToken);
                    if (TryGetRpcError(response.RootElement, out string error))
                    {
                        throw new InvalidOperationException(error);
                    }

                    if (!TryGetResult(response.RootElement, out var result))
                    {
                        break;
                    }

                    if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tool in tools.EnumerateArray())
                        {
                            string name = TryGetStringProperty(tool, "name");
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                continue;
                            }

                            session.Tools.Add(new AgentMcpTool
                            {
                                Name = name,
                                Description = TryGetStringProperty(tool, "description"),
                                InputSchemaJson = TryGetPropertyRawJson(tool, "inputSchema", "{}")
                            });
                        }
                    }

                    cursor = TryGetStringProperty(result, "nextCursor");
                }
                while (!string.IsNullOrEmpty(cursor));

                _serverStatus[server.Id] = string.Format(
                    _getString("AgentMcpStatusToolCount", "{0:N0}개 도구"),
                    session.Tools.Count);
            }
            catch (Exception ex)
            {
                _serverStatus[server.Id] = string.Format(
                    _getString("AgentMcpStatusFailedFormat", "연결 실패: {0}"),
                    ex.Message);
            }

            RebuildAliases();
            UpdateUI();
        }

        private async Task<AgentMcpSession> EnsureSessionAsync(
            AgentMcpServer server,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            if (!forceRefresh && _sessions.TryGetValue(server.Id, out var existing) && existing.Initialized)
            {
                return existing;
            }

            var session = new AgentMcpSession();
            _sessions[server.Id] = session;

            using JsonDocument initializeResponse = await SendJsonRpcAsync(
                server,
                session,
                "initialize",
                new Dictionary<string, object?>
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new Dictionary<string, object?>(),
                    ["clientInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "TxtAIEditor",
                        ["version"] = "1.0"
                    }
                },
                cancellationToken);

            if (TryGetRpcError(initializeResponse.RootElement, out string error))
            {
                throw new InvalidOperationException(error);
            }

            await SendJsonRpcNotificationAsync(server, session, "notifications/initialized", cancellationToken);
            session.Initialized = true;
            return session;
        }

        private async Task<JsonDocument> SendJsonRpcAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            int id = session.NextId++;
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>()
            };

            string responseText = await PostAsync(server, session, payload, cancellationToken);
            return ParseRpcResponse(responseText);
        }

        private async Task SendJsonRpcNotificationAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            string method,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            };

            try
            {
                await PostAsync(server, session, payload, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Some MCP servers return 202/empty for notifications; others close without a JSON body.
            }
        }

        private static async Task<string> PostAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            object payload,
            CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            foreach (var header in server.Headers)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(header.Value))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (!string.IsNullOrEmpty(session.SessionId))
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session.SessionId);
            }

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
            {
                session.SessionId = sessionIds.FirstOrDefault() ?? session.SessionId;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            return body;
        }

        private static JsonDocument ParseRpcResponse(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                throw new InvalidOperationException("MCP server returned an empty response.");
            }

            string trimmed = responseText.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return JsonDocument.Parse(responseText);
            }

            var dataBuilder = new StringBuilder();
            using var reader = new StringReader(responseText);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string data = line.Substring(5).TrimStart();
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                dataBuilder.Append(data);
            }

            string dataText = dataBuilder.ToString();
            if (string.IsNullOrWhiteSpace(dataText))
            {
                throw new InvalidOperationException("MCP server returned no JSON-RPC data.");
            }

            return JsonDocument.Parse(dataText);
        }

        private void RebuildAliases()
        {
            _toolAliases.Clear();
            var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var server in GetSelectedServers())
            {
                if (!_sessions.TryGetValue(server.Id, out var session))
                {
                    continue;
                }

                foreach (var tool in session.Tools)
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

        private void UpdateUI()
        {
            var items = _servers
                .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(server =>
                {
                    string detail = _serverStatus.TryGetValue(server.Id, out string? status) && !string.IsNullOrWhiteSpace(status)
                        ? $"{server.Endpoint} - {status}"
                        : server.Endpoint;
                    if (server.Headers.Count > 0)
                    {
                        detail += $" - headers: {string.Join(", ", server.Headers.Keys)}";
                    }

                    return new AgentMcpItem
                    {
                        Name = server.Name,
                        Endpoint = server.Endpoint,
                        Detail = detail,
                        IsSelected = _selectedServerIds.Contains(server.Id)
                    };
                })
                .ToList();
            var selectedNames = GetSelectedServers().Select(server => server.Name).ToList();
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.UpdateAgentMcpMenu(items, selectedNames, _getString);
                _contextChanged();
            });
        }

        private async Task SaveAsync()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_mcpFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(_servers, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_mcpFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save MCP servers: {ex.Message}");
            }

            RebuildAliases();
            UpdateUI();
        }

        private static List<AgentMcpServer> DeserializeImportedServers(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<AgentMcpServer>>(json) ?? new List<AgentMcpServer>();
            }

            var servers = new List<AgentMcpServer>();
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("mcpServers", out var mcpServers) ||
                mcpServers.ValueKind != JsonValueKind.Object)
            {
                return servers;
            }

            foreach (var property in mcpServers.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string endpoint = TryGetStringProperty(property.Value, "url");
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    endpoint = TryGetStringProperty(property.Value, "endpoint");
                }

                servers.Add(new AgentMcpServer
                {
                    Name = property.Name,
                    Endpoint = endpoint,
                    Headers = ReadHeadersProperty(property.Value)
                });
            }

            return servers;
        }

        private static Dictionary<string, string> ReadHeadersProperty(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("headers", out var headersElement) ||
                headersElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in headersElement.EnumerateObject())
            {
                string key = property.Name?.Trim() ?? string.Empty;
                string value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    headers[key] = value.Trim();
                }
            }

            return headers;
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
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    normalized[key] = value;
                }
            }

            return normalized;
        }

        private static bool TryBuildHeaders(string headerName, string apiKey, out Dictionary<string, string> headers)
        {
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headerName = headerName?.Trim() ?? string.Empty;
            apiKey = apiKey?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(headerName) && string.IsNullOrWhiteSpace(apiKey))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(apiKey))
            {
                return false;
            }

            headers[headerName] = apiKey;
            return true;
        }

        private AgentMcpServer? FindServer(string name)
        {
            return _servers.FirstOrDefault(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsValidHttpEndpoint(string endpoint)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private List<AgentMcpServer> GetSelectedServers()
        {
            return _servers
                .Where(server => _selectedServerIds.Contains(server.Id))
                .ToList();
        }

        private TextBox CreateTextBox(string placeholder)
        {
            return new TextBox
            {
                PlaceholderText = placeholder,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private PasswordBox CreatePasswordBox(string placeholder)
        {
            return new PasswordBox
            {
                PlaceholderText = placeholder,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                FontFamily = new FontFamily("Segoe UI, Malgun Gothic"),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            _beforeDialog?.Invoke();
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                _afterDialog?.Invoke();
            }
        }

        private static string FormatToolCallResult(AgentMcpToolAlias alias, JsonElement result)
        {
            bool isError = result.TryGetProperty("isError", out var isErrorProp) && isErrorProp.ValueKind == JsonValueKind.True;
            var builder = new StringBuilder();
            builder.AppendLine(isError
                ? $"MCP tool returned an error: {alias.Alias}"
                : $"MCP tool result: {alias.Alias}");

            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    string type = TryGetStringProperty(item, "type");
                    if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = TryGetStringProperty(item, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            builder.AppendLine(text);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(type))
                    {
                        builder.AppendLine($"[{type} content omitted]");
                    }
                }
            }

            if (result.TryGetProperty("structuredContent", out var structuredContent))
            {
                builder.AppendLine("[structuredContent]");
                builder.AppendLine(structuredContent.GetRawText());
            }

            string textResult = builder.ToString().TrimEnd();
            return string.IsNullOrWhiteSpace(textResult)
                ? result.GetRawText()
                : textResult;
        }

        private static bool TryGetRpcError(JsonElement root, out string error)
        {
            error = string.Empty;
            if (!root.TryGetProperty("error", out var errorElement))
            {
                return false;
            }

            if (errorElement.ValueKind == JsonValueKind.Object)
            {
                string message = TryGetStringProperty(errorElement, "message");
                string code = TryGetPropertyRawJson(errorElement, "code", string.Empty);
                error = string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
            }
            else
            {
                error = errorElement.GetRawText();
            }

            return true;
        }

        private static bool TryGetResult(JsonElement root, out JsonElement result)
        {
            if (root.TryGetProperty("result", out result))
            {
                return true;
            }

            result = default;
            return false;
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

        private static string TryGetPropertyRawJson(JsonElement element, string propertyName, string fallback)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return fallback;
            }

            return property.GetRawText();
        }

        private static string SanitizeToolSegment(string value)
        {
            string normalized = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_]+", "_").Trim('_').ToLowerInvariant();
            normalized = Regex.Replace(normalized, @"_+", "_");
            return string.IsNullOrWhiteSpace(normalized) ? "server" : normalized;
        }

        private sealed class AgentMcpSession
        {
            public string SessionId { get; set; } = string.Empty;
            public bool Initialized { get; set; }
            public int NextId { get; set; } = 1;
            public List<AgentMcpTool> Tools { get; } = new();
        }

        private sealed class AgentMcpTool
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string InputSchemaJson { get; set; } = "{}";
        }
    }
}
