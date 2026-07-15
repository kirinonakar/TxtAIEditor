using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    internal sealed class McpToolClient
    {
        public async Task<string> CallToolAsync(
            string endpointUrl,
            string apiKey,
            string toolName,
            object arguments,
            CancellationToken cancellationToken)
        {
            if (endpointUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("MCP endpoint must use HTTPS.");
            }

            if (!endpointUrl.Contains("/sse", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return await CallMcpHttpToolAsync(endpointUrl, apiKey, toolName, arguments, cancellationToken);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MCP HTTP transport failed, falling back to SSE transport: {ex.Message}");
                }
            }

            using (var sseClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                using (var sseRequest = new HttpRequestMessage(HttpMethod.Get, endpointUrl))
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        sseRequest.Headers.Add("x-api-key", apiKey);
                    }
                    sseRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using (var sseResponse = await sseClient.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if (!sseResponse.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"MCP SSE connection failed: {sseResponse.StatusCode}");
                        }

                        using (System.IO.Stream stream = await sseResponse.Content.ReadAsStreamAsync(cancellationToken))
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string? postEndpoint = null;
                            string? currentEvent = null;
                            var currentData = new StringBuilder();

                            // Step 1: Read SSE stream until we get the post endpoint
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string? line = await reader.ReadLineAsync();
                                if (line == null) break;

                                if (string.IsNullOrWhiteSpace(line))
                                {
                                    if (currentEvent == "endpoint" && currentData.Length > 0)
                                    {
                                        postEndpoint = ParseEndpointUri(currentData.ToString().Trim());
                                        break;
                                    }
                                    currentEvent = null;
                                    currentData.Clear();
                                    continue;
                                }

                                if (line.StartsWith("event:"))
                                {
                                    currentEvent = line.Substring(6).Trim();
                                }
                                else if (line.StartsWith("data:"))
                                {
                                    if (currentData.Length > 0)
                                    {
                                        currentData.Append("\n");
                                    }
                                    currentData.Append(line.Substring(5).Trim());
                                }
                            }

                            // Edge case fallback if connection ends before a blank line
                            if (postEndpoint == null && currentEvent == "endpoint" && currentData.Length > 0)
                            {
                                postEndpoint = ParseEndpointUri(currentData.ToString().Trim());
                            }

                            if (string.IsNullOrEmpty(postEndpoint))
                            {
                                throw new InvalidOperationException("MCP server did not provide a post endpoint.");
                            }

                            // Resolve absolute post URL
                            string postUrl = postEndpoint;
                            if (!postUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                                !postUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                var baseUri = new Uri(endpointUrl);
                                var resolvedUri = new Uri(baseUri, postUrl);
                                postUrl = resolvedUri.ToString();
                            }

                            using (var postClient = new HttpClient())
                            {
                                // Step 2: Send initialize request
                                string initId = "init_" + Guid.NewGuid().ToString("N");
                                var initPayload = new
                                {
                                    jsonrpc = "2.0",
                                    method = "initialize",
                                    @params = new
                                    {
                                        protocolVersion = "2024-11-05",
                                        capabilities = new { },
                                        clientInfo = new
                                        {
                                            name = "TxtAIEditor",
                                            version = "1.0.0"
                                        }
                                    },
                                    id = initId
                                };

                                string initJson = JsonSerializer.Serialize(initPayload);
                                using (var initRequest = new HttpRequestMessage(HttpMethod.Post, postUrl))
                                {
                                    if (!string.IsNullOrEmpty(apiKey))
                                    {
                                        initRequest.Headers.Add("x-api-key", apiKey);
                                    }
                                    initRequest.Content = new StringContent(initJson, Encoding.UTF8, "application/json");

                                    var initResponse = await postClient.SendAsync(initRequest, cancellationToken);
                                    if (!initResponse.IsSuccessStatusCode)
                                    {
                                        string errBody = await initResponse.Content.ReadAsStringAsync(cancellationToken);
                                        throw new HttpRequestException($"MCP initialization POST failed: {initResponse.StatusCode}\n{errBody}");
                                    }
                                }

                                // Step 3: Wait for initialize response on SSE stream
                                currentEvent = null;
                                currentData.Clear();
                                bool initialized = false;

                                while (!initialized)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    string? line = await reader.ReadLineAsync();
                                    if (line == null) break;

                                    if (string.IsNullOrWhiteSpace(line))
                                    {
                                        if (currentEvent == "message" && currentData.Length > 0)
                                        {
                                            string dataValue = currentData.ToString().Trim();
                                            using (var doc = JsonDocument.Parse(dataValue))
                                            {
                                                var root = doc.RootElement;
                                                if (root.TryGetProperty("id", out var idProp) && idProp.GetString() == initId)
                                                {
                                                    if (root.TryGetProperty("error", out var errorProp))
                                                    {
                                                        throw new InvalidOperationException($"MCP initialization failed: {errorProp.GetRawText()}");
                                                    }
                                                    initialized = true;
                                                }
                                            }
                                        }
                                        currentEvent = null;
                                        currentData.Clear();
                                        continue;
                                    }

                                    if (line.StartsWith("event:"))
                                    {
                                        currentEvent = line.Substring(6).Trim();
                                    }
                                    else if (line.StartsWith("data:"))
                                    {
                                        if (currentData.Length > 0)
                                        {
                                            currentData.Append("\n");
                                        }
                                        currentData.Append(line.Substring(5).Trim());
                                    }
                                }

                                if (!initialized)
                                {
                                    throw new InvalidOperationException("MCP connection closed before initialization response was received.");
                                }

                                // Step 4: Send notifications/initialized notification
                                var initializedNotification = new
                                {
                                    jsonrpc = "2.0",
                                    method = "notifications/initialized"
                                };
                                string notificationJson = JsonSerializer.Serialize(initializedNotification);
                                using (var notificationRequest = new HttpRequestMessage(HttpMethod.Post, postUrl))
                                {
                                    if (!string.IsNullOrEmpty(apiKey))
                                    {
                                        notificationRequest.Headers.Add("x-api-key", apiKey);
                                    }
                                    notificationRequest.Content = new StringContent(notificationJson, Encoding.UTF8, "application/json");
                                    await postClient.SendAsync(notificationRequest, cancellationToken);
                                }

                                // Step 5: Send tools/call request
                                string callId = "call_" + Guid.NewGuid().ToString("N");
                                var callPayload = new
                                {
                                    jsonrpc = "2.0",
                                    method = "tools/call",
                                    @params = new
                                    {
                                        name = toolName,
                                        arguments = arguments
                                    },
                                    id = callId
                                };
                                string callJson = JsonSerializer.Serialize(callPayload);
                                using (var callRequest = new HttpRequestMessage(HttpMethod.Post, postUrl))
                                {
                                    if (!string.IsNullOrEmpty(apiKey))
                                    {
                                        callRequest.Headers.Add("x-api-key", apiKey);
                                    }
                                    callRequest.Content = new StringContent(callJson, Encoding.UTF8, "application/json");

                                    var callResponse = await postClient.SendAsync(callRequest, cancellationToken);
                                    if (!callResponse.IsSuccessStatusCode)
                                    {
                                        string errBody = await callResponse.Content.ReadAsStringAsync(cancellationToken);
                                        throw new HttpRequestException($"MCP tool call POST failed: {callResponse.StatusCode}\n{errBody}");
                                    }
                                }

                                // Step 6: Wait for tool response on SSE stream
                                currentEvent = null;
                                currentData.Clear();
                                while (true)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    string? line = await reader.ReadLineAsync();
                                    if (line == null) break;

                                    if (string.IsNullOrWhiteSpace(line))
                                    {
                                        if (currentEvent == "message" && currentData.Length > 0)
                                        {
                                            string dataValue = currentData.ToString().Trim();
                                            using (var doc = JsonDocument.Parse(dataValue))
                                            {
                                                var root = doc.RootElement;
                                                if (root.TryGetProperty("id", out var idProp) && idProp.GetString() == callId)
                                                {
                                                    if (root.TryGetProperty("error", out var errorProp))
                                                    {
                                                        throw new InvalidOperationException($"MCP tool execution failed: {errorProp.GetRawText()}");
                                                    }

                                                    if (root.TryGetProperty("result", out var resultProp))
                                                    {
                                                        return FormatMcpSearchResult(resultProp);
                                                    }
                                                }
                                            }
                                        }
                                        currentEvent = null;
                                        currentData.Clear();
                                        continue;
                                    }

                                    if (line.StartsWith("event:"))
                                    {
                                        currentEvent = line.Substring(6).Trim();
                                    }
                                    else if (line.StartsWith("data:"))
                                    {
                                        if (currentData.Length > 0)
                                        {
                                            currentData.Append("\n");
                                        }
                                        currentData.Append(line.Substring(5).Trim());
                                    }
                                }
                            }
                        }
                    }
                }
            }

            throw new InvalidOperationException("MCP connection closed without response.");
        }

        private async Task<string> CallMcpHttpToolAsync(
            string endpointUrl,
            string apiKey,
            string toolName,
            object arguments,
            CancellationToken cancellationToken)
        {
            if (!endpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("MCP endpoint must use HTTPS.");
            }

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                string initId = "init_" + Guid.NewGuid().ToString("N");
                var initPayload = new
                {
                    jsonrpc = "2.0",
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { },
                        clientInfo = new
                        {
                            name = "TxtAIEditor",
                            version = "1.0.0"
                        }
                    },
                    id = initId
                };

                using (var initResponse = await SendMcpHttpRequestAsync(client, endpointUrl, apiKey, null, initPayload, cancellationToken))
                {
                    string initBody = await initResponse.Content.ReadAsStringAsync(cancellationToken);
                    if (!initResponse.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"MCP HTTP initialize failed: {initResponse.StatusCode}\n{initBody}");
                    }

                    JsonElement initRoot = ParseMcpHttpResponse(initBody, initId);
                    if (initRoot.TryGetProperty("error", out var initErrorProp))
                    {
                        throw new InvalidOperationException($"MCP HTTP initialize failed: {initErrorProp.GetRawText()}");
                    }

                    string sessionId = GetMcpSessionId(initResponse);
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        throw new InvalidOperationException("MCP HTTP server did not return Mcp-Session-Id.");
                    }

                    var initializedNotification = new
                    {
                        jsonrpc = "2.0",
                        method = "notifications/initialized"
                    };
                    using (var notificationResponse = await SendMcpHttpRequestAsync(client, endpointUrl, apiKey, sessionId, initializedNotification, cancellationToken))
                    {
                        if (!notificationResponse.IsSuccessStatusCode)
                        {
                            string errBody = await notificationResponse.Content.ReadAsStringAsync(cancellationToken);
                            throw new HttpRequestException($"MCP HTTP initialized notification failed: {notificationResponse.StatusCode}\n{errBody}");
                        }
                    }

                    string callId = "call_" + Guid.NewGuid().ToString("N");
                    var callPayload = new
                    {
                        jsonrpc = "2.0",
                        method = "tools/call",
                        @params = new
                        {
                            name = toolName,
                            arguments = arguments
                        },
                        id = callId
                    };

                    using (var callResponse = await SendMcpHttpRequestAsync(client, endpointUrl, apiKey, sessionId, callPayload, cancellationToken))
                    {
                        string callBody = await callResponse.Content.ReadAsStringAsync(cancellationToken);
                        if (!callResponse.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"MCP HTTP tool call failed: {callResponse.StatusCode}\n{callBody}");
                        }

                        JsonElement callRoot = ParseMcpHttpResponse(callBody, callId);
                        if (callRoot.TryGetProperty("error", out var callErrorProp))
                        {
                            throw new InvalidOperationException($"MCP HTTP tool execution failed: {callErrorProp.GetRawText()}");
                        }

                        if (callRoot.TryGetProperty("result", out var resultProp))
                        {
                            return FormatMcpSearchResult(resultProp);
                        }

                        return callRoot.GetRawText();
                    }
                }
            }
        }

        private static async Task<HttpResponseMessage> SendMcpHttpRequestAsync(
            HttpClient client,
            string endpointUrl,
            string apiKey,
            string? sessionId,
            object payload,
            CancellationToken cancellationToken)
        {
            string jsonPayload = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("x-api-key", apiKey);
            }
            if (!string.IsNullOrEmpty(sessionId))
            {
                request.Headers.Add("Mcp-Session-Id", sessionId);
            }
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await client.SendAsync(request, cancellationToken);
        }

        private static string GetMcpSessionId(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                foreach (string value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }

            return string.Empty;
        }

        private static JsonElement ParseMcpHttpResponse(string responseBody, string expectedId)
        {
            string json = ExtractMcpSseJson(responseBody, expectedId);
            using (var doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        if (JsonRpcIdMatches(item, expectedId))
                        {
                            return item.Clone();
                        }
                    }

                    if (root.GetArrayLength() > 0)
                    {
                        return root[0].Clone();
                    }
                }

                return root.Clone();
            }
        }

        private static string ExtractMcpSseJson(string responseBody, string expectedId)
        {
            string trimmed = responseBody.Trim();
            if (!trimmed.StartsWith("event:", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            string? currentEvent = null;
            var currentData = new StringBuilder();
            string? firstData = null;

            using (var reader = new System.IO.StringReader(responseBody))
            {
                while (true)
                {
                    string? line = reader.ReadLine();
                    if (line == null || string.IsNullOrWhiteSpace(line))
                    {
                        string? dataValue = FlushMcpSseData(currentEvent, currentData);
                        if (!string.IsNullOrWhiteSpace(dataValue))
                        {
                            firstData ??= dataValue;
                            try
                            {
                                using (var doc = JsonDocument.Parse(dataValue))
                                {
                                    if (JsonRpcIdMatches(doc.RootElement, expectedId))
                                    {
                                        return dataValue;
                                    }
                                }
                            }
                            catch
                            {
                                // Keep looking for a JSON-RPC message.
                            }
                        }

                        currentEvent = null;
                        currentData.Clear();
                        if (line == null)
                        {
                            break;
                        }
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEvent = line.Substring(6).Trim();
                    }
                    else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currentData.Length > 0)
                        {
                            currentData.Append("\n");
                        }
                        currentData.Append(line.Substring(5).Trim());
                    }
                }
            }

            return firstData ?? trimmed;
        }

        private static string? FlushMcpSseData(string? currentEvent, StringBuilder currentData)
        {
            if (currentData.Length == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(currentEvent) &&
                !currentEvent.Equals("message", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return currentData.ToString().Trim();
        }

        private static bool JsonRpcIdMatches(JsonElement root, string expectedId)
        {
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.String &&
                string.Equals(idProp.GetString(), expectedId, StringComparison.Ordinal);
        }

        private static string ParseEndpointUri(string data)
        {
            data = data.Trim();
            if (data.StartsWith("{") && data.EndsWith("}"))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(data))
                    {
                        if (doc.RootElement.TryGetProperty("uri", out var uriProp))
                        {
                            return uriProp.GetString() ?? data;
                        }
                    }
                }
                catch
                {
                    // Fallback to raw string
                }
            }
            return data;
        }

        private string FormatMcpSearchResult(JsonElement resultProp)
        {
            if (!resultProp.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.Array)
            {
                return resultProp.GetRawText();
            }

            var sb = new StringBuilder();
            foreach (var item in contentProp.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "text")
                {
                    if (item.TryGetProperty("text", out var textProp))
                    {
                        sb.AppendLine(textProp.GetString());
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

    }
}
