using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static TxtAIEditor.Controls.AgentMcpAuthTypes;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentMcpRuntime : IDisposable
    {
        private const string LegacyProtocolVersion = "2025-06-18";
        private const string StatelessProtocolVersion = "2026-07-28";
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        private static readonly HttpClient AgentPluginHttpClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly AgentMcpCredentialStore _credentialStore;
        private readonly AgentMcpOAuthService _oauthService;
        private readonly Func<string> _workspaceRootProvider;
        private readonly Func<string, string, string> _getString;
        private readonly Dictionary<string, AgentMcpSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

        public AgentMcpRuntime(
            AgentMcpCredentialStore credentialStore,
            AgentMcpOAuthService oauthService,
            Func<string> workspaceRootProvider,
            Func<string, string, string> getString)
        {
            _credentialStore = credentialStore;
            _oauthService = oauthService;
            _workspaceRootProvider = workspaceRootProvider;
            _getString = getString;
        }

        public IReadOnlyList<AgentMcpTool> GetTools(string serverId)
        {
            return _sessions.TryGetValue(serverId, out var session)
                ? session.Tools
                : Array.Empty<AgentMcpTool>();
        }

        public async Task<IReadOnlyList<AgentMcpTool>> RefreshToolsAsync(
            AgentMcpServer server,
            CancellationToken cancellationToken)
        {
            AgentMcpSession? session = null;
            try
            {
                session = await EnsureSessionAsync(server, cancellationToken, forceRefresh: true);
                session.Tools.Clear();

                string? cursor = null;
                do
                {
                    Dictionary<string, object?> parameters = new();
                    if (!string.IsNullOrEmpty(cursor))
                    {
                        parameters["cursor"] = cursor;
                    }

                    using JsonDocument response = await SendMcpRequestAsync(server, session, "tools/list", parameters, cancellationToken);
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

                return session.Tools;
            }
            catch
            {
                RemoveSession(server.Id, session);
                throw;
            }
        }

        public async Task<JsonDocument> ExecuteToolAsync(
            AgentMcpServer server,
            string toolName,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            AgentMcpSession session = await EnsureSessionAsync(server, cancellationToken);
            JsonElement callArguments = arguments.ValueKind == JsonValueKind.Object
                ? arguments.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();

            return await SendMcpRequestAsync(
                server,
                session,
                "tools/call",
                new Dictionary<string, object?>
                {
                    ["name"] = toolName,
                    ["arguments"] = callArguments
                },
                cancellationToken,
                toolName);
        }

        public void RemoveSession(string serverId)
        {
            RemoveSession(serverId, expectedSession: null);
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }

            _sessions.Clear();
        }

        private async Task<AgentMcpSession> EnsureSessionAsync(
            AgentMcpServer server,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            if (!forceRefresh &&
                _sessions.TryGetValue(server.Id, out var existing) &&
                existing.Initialized &&
                (!AgentMcpTransportTypes.IsStdio(server.Transport) || existing.Process?.HasExited == false))
            {
                return existing;
            }

            RemoveSession(server.Id);
            var session = new AgentMcpSession
            {
                UsesStatelessProtocol = !AgentMcpTransportTypes.IsStdio(server.Transport) && server.Stateless,
                ProtocolVersion = !AgentMcpTransportTypes.IsStdio(server.Transport) && server.Stateless
                    ? StatelessProtocolVersion
                    : LegacyProtocolVersion
            };
            if (AgentMcpTransportTypes.IsStdio(server.Transport))
            {
                StartStdioProcess(server, session);
            }
            _sessions[server.Id] = session;

            try
            {
                if (session.UsesStatelessProtocol)
                {
                    session.Initialized = true;
                    return session;
                }

                using JsonDocument initializeResponse = await SendJsonRpcAsync(
                    server,
                    session,
                    "initialize",
                    CreateInitializeParameters(LegacyProtocolVersion),
                    cancellationToken);

                EnsureSuccessfulResponse(initializeResponse.RootElement);
                string negotiatedProtocolVersion = TryGetStringProperty(
                    initializeResponse.RootElement.TryGetProperty("result", out var initializeResult)
                        ? initializeResult
                        : default,
                    "protocolVersion");
                if (!string.IsNullOrWhiteSpace(negotiatedProtocolVersion))
                {
                    session.ProtocolVersion = negotiatedProtocolVersion;
                }
                await SendJsonRpcNotificationAsync(server, session, "notifications/initialized", cancellationToken);
                session.Initialized = true;
                return session;
            }
            catch
            {
                RemoveSession(server.Id, session);
                throw;
            }
        }

        private async Task<JsonDocument> SendJsonRpcAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            string method,
            object? parameters,
            CancellationToken cancellationToken,
            string? mcpName = null)
        {
            int id = session.NextId++;
            object requestParameters = session.UsesStatelessProtocol
                ? CreateStatelessParameters(parameters)
                : parameters ?? new Dictionary<string, object?>();
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = requestParameters
            };

            string responseText = await PostAsync(server, session, method, mcpName, payload, cancellationToken);
            return ParseRpcResponse(responseText);
        }

        private async Task<JsonDocument> SendMcpRequestAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            string method,
            object? parameters,
            CancellationToken cancellationToken,
            string? mcpName = null)
        {
            if (!session.UsesStatelessProtocol)
            {
                return await SendJsonRpcAsync(server, session, method, parameters, cancellationToken, mcpName);
            }

            try
            {
                JsonDocument response = await SendJsonRpcAsync(
                    server,
                    session,
                    method,
                    parameters,
                    cancellationToken,
                    mcpName);
                if (!IsStatelessCompatibilityError(response.RootElement))
                {
                    return response;
                }

                response.Dispose();
            }
            catch (McpHttpException ex) when (IsStatelessCompatibilityStatus(ex.StatusCode))
            {
            }

            await InitializeLegacyStatelessSessionAsync(server, session, cancellationToken);
            return await SendJsonRpcAsync(server, session, method, parameters, cancellationToken, mcpName);
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

            if (AgentMcpTransportTypes.IsStdio(server.Transport))
            {
                await WriteStdioMessageAsync(server, session, payload, cancellationToken);
                return;
            }

            try
            {
                await PostAsync(server, session, method, null, payload, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Some MCP servers return 202/empty for notifications; others close without a JSON body.
            }
        }

        private async Task InitializeLegacyStatelessSessionAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            CancellationToken cancellationToken)
        {
            session.UsesStatelessProtocol = false;
            session.ProtocolVersion = LegacyProtocolVersion;
            session.UseSessionId = false;

            using JsonDocument initializeResponse = await SendJsonRpcAsync(
                server,
                session,
                "initialize",
                CreateInitializeParameters(LegacyProtocolVersion),
                cancellationToken);

            EnsureSuccessfulResponse(initializeResponse.RootElement);
            string negotiatedProtocolVersion = TryGetStringProperty(
                initializeResponse.RootElement.TryGetProperty("result", out var initializeResult)
                    ? initializeResult
                    : default,
                "protocolVersion");
            if (!string.IsNullOrWhiteSpace(negotiatedProtocolVersion))
            {
                session.ProtocolVersion = negotiatedProtocolVersion;
            }

            await SendJsonRpcNotificationAsync(server, session, "notifications/initialized", cancellationToken);
        }

        private static Dictionary<string, object?> CreateInitializeParameters(string protocolVersion)
        {
            return new Dictionary<string, object?>
            {
                ["protocolVersion"] = protocolVersion,
                ["capabilities"] = new Dictionary<string, object?>(),
                ["clientInfo"] = new Dictionary<string, object?>
                {
                    ["name"] = "TxtAIEditor",
                    ["version"] = "1.0"
                }
            };
        }

        private static Dictionary<string, object?> CreateStatelessParameters(object? parameters)
        {
            var statelessParameters = parameters is IDictionary<string, object?> dictionary
                ? new Dictionary<string, object?>(dictionary, StringComparer.Ordinal)
                : new Dictionary<string, object?>();
            statelessParameters["_meta"] = new Dictionary<string, object?>
            {
                ["io.modelcontextprotocol/protocolVersion"] = StatelessProtocolVersion,
                ["io.modelcontextprotocol/clientInfo"] = new Dictionary<string, object?>
                {
                    ["name"] = "TxtAIEditor",
                    ["version"] = "1.0"
                },
                ["io.modelcontextprotocol/clientCapabilities"] = new Dictionary<string, object?>()
            };
            return statelessParameters;
        }

        private static void EnsureSuccessfulResponse(JsonElement root)
        {
            if (TryGetRpcError(root, out string error))
            {
                throw new InvalidOperationException(error);
            }
        }

        private static bool IsStatelessCompatibilityError(JsonElement root)
        {
            if (!root.TryGetProperty("error", out var error) ||
                !error.TryGetProperty("code", out var code) ||
                !code.TryGetInt32(out int errorCode))
            {
                return false;
            }

            return errorCode == -32600 ||
                errorCode == -32602 ||
                errorCode == -32601 ||
                errorCode == -32000 ||
                (errorCode >= -32022 && errorCode <= -32020);
        }

        private static bool IsStatelessCompatibilityStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.BadRequest ||
                statusCode == HttpStatusCode.NotFound ||
                statusCode == HttpStatusCode.MethodNotAllowed ||
                statusCode == HttpStatusCode.UnsupportedMediaType;
        }

        private async Task<string> PostAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            string method,
            string? mcpName,
            object payload,
            CancellationToken cancellationToken)
        {
            if (AgentMcpTransportTypes.IsStdio(server.Transport))
            {
                return await SendStdioRequestAsync(server, session, payload, cancellationToken);
            }

            string json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (!method.Equals("initialize", StringComparison.Ordinal))
            {
                request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", session.ProtocolVersion);
            }
            if (session.UsesStatelessProtocol)
            {
                request.Headers.TryAddWithoutValidation("Mcp-Method", method);
                if (!string.IsNullOrWhiteSpace(mcpName))
                {
                    request.Headers.TryAddWithoutValidation("Mcp-Name", mcpName);
                }
            }
            if (server.AuthType.Equals(AuthTypeApiKey, StringComparison.OrdinalIgnoreCase) || server.IsAgentPlugin)
            {
                foreach (var header in server.Headers)
                {
                    string headerValue = _credentialStore.GetHeaderSecret(server, header.Key, header.Value);
                    if (!string.IsNullOrWhiteSpace(header.Key) && !string.IsNullOrWhiteSpace(headerValue))
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, headerValue);
                    }
                }
            }
            else if (IsOAuthAuthType(server.AuthType))
            {
                string accessToken = await _oauthService.EnsureAccessTokenAsync(server, cancellationToken);
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }
            }

            if (session.UseSessionId && !string.IsNullOrEmpty(session.SessionId))
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session.SessionId);
            }

            HttpClient httpClient = server.IsAgentPlugin ? AgentPluginHttpClient : HttpClient;
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (!session.UsesStatelessProtocol && response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
            {
                string sessionId = sessionIds.FirstOrDefault() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    session.SessionId = sessionId;
                    session.UseSessionId = true;
                }
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new McpHttpException(
                    response.StatusCode,
                    _credentialStore.RedactServerSecrets(server, $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}"));
            }

            return body;
        }

        private void StartStdioProcess(AgentMcpServer server, AgentMcpSession session)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
            {
                throw new InvalidOperationException(_getString("AgentMcpCommandRequired", "stdio 실행 명령을 입력해주세요."));
            }

            var process = new Process { StartInfo = CreateStdioStartInfo(server), EnableRaisingEvents = true };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException($"Failed to start MCP command: {server.Command}");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }

            session.Process = process;
            session.StandardInput = process.StandardInput;
            session.StandardOutput = process.StandardOutput;
            session.StandardInput.AutoFlush = true;
            session.StderrPump = PumpStderrAsync(session, process.StandardError);
        }

        private ProcessStartInfo CreateStdioStartInfo(AgentMcpServer server)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveCommand(AgentPluginLoader.ResolveRuntimeCommand(server)),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string argument in server.Arguments)
            {
                startInfo.ArgumentList.Add(AgentPluginLoader.ExpandRuntimeVariables(server, argument));
            }

            string targetDirectory = string.IsNullOrWhiteSpace(server.TargetDirectory)
                ? string.Empty
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(server.TargetDirectory.Trim().Trim('"')));
            bool isMemoryServer = IsMemoryServer(server);
            if (!string.IsNullOrWhiteSpace(targetDirectory) && !isMemoryServer)
            {
                startInfo.ArgumentList.Add(targetDirectory);
            }

            string workingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory)
                ? _workspaceRootProvider()
                : Environment.ExpandEnvironmentVariables(server.WorkingDirectory.Trim().Trim('"'));
            if (server.IsAgentPlugin)
            {
                Directory.CreateDirectory(server.AgentPluginDataDirectory);
                workingDirectory = AgentPluginLoader.ResolveRuntimeWorkingDirectory(server);
            }
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                startInfo.WorkingDirectory = Path.GetFullPath(workingDirectory);
            }

            foreach (var variable in server.Environment)
            {
                string value = _credentialStore.GetEnvironmentSecret(server, variable.Key, variable.Value);
                startInfo.Environment[variable.Key] = AgentPluginLoader.ExpandRuntimeVariables(server, value);
            }
            if (!string.IsNullOrWhiteSpace(targetDirectory) && isMemoryServer)
            {
                startInfo.Environment["MEMORY_FILE_PATH"] = Path.Combine(targetDirectory, "memory.jsonl");
            }
            if (server.IsAgentPlugin)
            {
                startInfo.Environment["PLUGIN_ROOT"] = server.AgentPluginRoot;
                startInfo.Environment["PLUGIN_DATA"] = server.AgentPluginDataDirectory;
            }

            return startInfo;
        }

        internal static bool IsMemoryServer(AgentMcpServer server)
        {
            const string packageName = "@modelcontextprotocol/server-memory";

            static bool IsMemoryPackage(string value, string expectedPackage)
            {
                string normalized = value.Trim().Trim('"');
                return normalized.Equals(expectedPackage, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(expectedPackage + "@", StringComparison.OrdinalIgnoreCase);
            }

            if (IsMemoryPackage(server.Command, packageName) ||
                server.Arguments.Any(argument => IsMemoryPackage(argument, packageName)))
            {
                return true;
            }

            string executableName = Path.GetFileNameWithoutExtension(server.Command.Trim().Trim('"'));
            return executableName.Equals("mcp-server-memory", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> SendStdioRequestAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            object payload,
            CancellationToken cancellationToken)
        {
            if (session.Process == null ||
                session.StandardInput == null ||
                session.StandardOutput == null ||
                session.Process.HasExited)
            {
                throw new InvalidOperationException("MCP stdio process is not running.");
            }

            int expectedId = GetPayloadId(payload);
            await session.StdioLock.WaitAsync(cancellationToken);
            try
            {
                await WriteStdioMessageCoreAsync(session, payload, cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await session.StandardOutput.ReadLineAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("MCP stdio server did not respond within 2 minutes.");
                    }

                    if (line == null)
                    {
                        throw new InvalidOperationException(BuildStdioExitMessage(server, session));
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    JsonDocument response;
                    try
                    {
                        response = JsonDocument.Parse(line);
                    }
                    catch (JsonException)
                    {
                        throw new InvalidOperationException($"MCP stdio server wrote invalid JSON to stdout: {line}");
                    }

                    if (RpcIdMatches(response.RootElement, expectedId))
                    {
                        return response.RootElement.GetRawText();
                    }

                    response.Dispose();
                }
            }
            finally
            {
                session.StdioLock.Release();
            }
        }

        private async Task WriteStdioMessageAsync(
            AgentMcpServer server,
            AgentMcpSession session,
            object payload,
            CancellationToken cancellationToken)
        {
            if (session.Process == null || session.StandardInput == null || session.Process.HasExited)
            {
                throw new InvalidOperationException(BuildStdioExitMessage(server, session));
            }

            await session.StdioLock.WaitAsync(cancellationToken);
            try
            {
                await WriteStdioMessageCoreAsync(session, payload, cancellationToken);
            }
            finally
            {
                session.StdioLock.Release();
            }
        }

        private static async Task WriteStdioMessageCoreAsync(
            AgentMcpSession session,
            object payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json = JsonSerializer.Serialize(payload);
            await session.StandardInput!.WriteLineAsync(json.AsMemory(), cancellationToken);
            await session.StandardInput.FlushAsync(cancellationToken);
        }

        private static async Task PumpStderrAsync(AgentMcpSession session, StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync() is string line)
                {
                    lock (session.StderrLines)
                    {
                        session.StderrLines.Enqueue(line);
                        while (session.StderrLines.Count > 20)
                        {
                            session.StderrLines.Dequeue();
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static int GetPayloadId(object payload)
        {
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            if (document.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out int value))
            {
                return value;
            }

            throw new InvalidOperationException("MCP JSON-RPC request has no numeric id.");
        }

        private static bool RpcIdMatches(JsonElement element, int expectedId)
        {
            return element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt32(out int value) &&
                value == expectedId;
        }

        private static string ResolveCommand(string command)
        {
            command = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
            if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            {
                return command;
            }

            string[] extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string normalizedDirectory = directory.Trim('"');
                string directCandidate = Path.Combine(normalizedDirectory, command);
                if (!Path.HasExtension(command))
                {
                    foreach (string extension in extensions)
                    {
                        string candidate = directCandidate + extension;
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }

                if (File.Exists(directCandidate))
                {
                    return directCandidate;
                }
            }

            return command;
        }

        private static string BuildStdioExitMessage(AgentMcpServer server, AgentMcpSession session)
        {
            string stderr;
            lock (session.StderrLines)
            {
                stderr = string.Join(Environment.NewLine, session.StderrLines);
            }

            string exit = session.Process?.HasExited == true
                ? $" (exit code {session.Process.ExitCode})"
                : string.Empty;
            return string.IsNullOrWhiteSpace(stderr)
                ? $"MCP stdio process '{server.Command}' closed{exit}."
                : $"MCP stdio process '{server.Command}' closed{exit}: {stderr}";
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

        private void RemoveSession(string serverId, AgentMcpSession? expectedSession)
        {
            if (_sessions.TryGetValue(serverId, out var session) &&
                (expectedSession == null || ReferenceEquals(session, expectedSession)))
            {
                _sessions.Remove(serverId);
                session.Dispose();
            }
        }

        internal static bool TryGetRpcError(JsonElement root, out string error)
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

        internal static bool TryGetResult(JsonElement root, out JsonElement result)
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

        private sealed class McpHttpException : InvalidOperationException
        {
            public McpHttpException(HttpStatusCode statusCode, string message)
                : base(message)
            {
                StatusCode = statusCode;
            }

            public HttpStatusCode StatusCode { get; }
        }

        private sealed class AgentMcpSession : IDisposable
        {
            private int _disposed;

            public string SessionId { get; set; } = string.Empty;
            public string ProtocolVersion { get; set; } = LegacyProtocolVersion;
            public bool UsesStatelessProtocol { get; set; }
            public bool UseSessionId { get; set; }
            public bool Initialized { get; set; }
            public int NextId { get; set; } = 1;
            public List<AgentMcpTool> Tools { get; } = new();
            public Process? Process { get; set; }
            public StreamWriter? StandardInput { get; set; }
            public StreamReader? StandardOutput { get; set; }
            public SemaphoreSlim StdioLock { get; } = new(1, 1);
            public Queue<string> StderrLines { get; } = new();
            public Task? StderrPump { get; set; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                Process? process = Process;
                try
                {
                    if (process is { HasExited: false })
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(2000);
                    }
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
                {
                }

                try
                {
                    StandardInput?.Dispose();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }

                try
                {
                    StandardOutput?.Dispose();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }

                process?.Dispose();
            }
        }
    }

    internal sealed class AgentMcpTool
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string InputSchemaJson { get; set; } = "{}";
    }
}
