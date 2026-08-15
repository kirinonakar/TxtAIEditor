using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    internal static class LlmResponsesApiClient
    {
        private static readonly ConcurrentDictionary<string, bool> _supportCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

        public static async Task<bool> SupportsAsync(
            HttpClient httpClient,
            string endpoint,
            string apiKey,
            string model,
            CancellationToken cancellationToken = default,
            Action<HttpRequestMessage>? configureRequest = null)
        {
            string requestUrl = BuildRequestUrl(endpoint);
            if (string.IsNullOrWhiteSpace(requestUrl))
            {
                return false;
            }

            string cacheKey = requestUrl + "\n" + (model ?? string.Empty);
            if (_supportCache.TryGetValue(cacheKey, out bool cachedResult))
            {
                return cachedResult;
            }

            using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCancellation.CancelAfter(ProbeTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            AddAuthorization(request, apiKey);
            configureRequest?.Invoke(request);

            // A null input is intentionally invalid. A valid Responses endpoint should
            // reject this request during validation without starting model generation.
            var probePayload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["input"] = null
            };
            request.Content = new StringContent(
                JsonSerializer.Serialize(probePayload),
                Encoding.UTF8,
                "application/json");

            try
            {
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    probeCancellation.Token);
                string responseBody = await response.Content.ReadAsStringAsync(probeCancellation.Token);
                bool? supported = ClassifyProbeResponse(response.StatusCode, responseBody);
                if (supported.HasValue)
                {
                    _supportCache[cacheKey] = supported.Value;
                    return supported.Value;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (HttpRequestException)
            {
                return false;
            }

            return false;
        }

        public static async Task<string> GenerateCompletionAsync(
            HttpClient httpClient,
            string endpoint,
            string apiKey,
            string model,
            string systemPrompt,
            string userContent,
            int outputLimit,
            string? reasoningEffort,
            IReadOnlyList<LlmMessageAttachment>? attachments,
            IReadOnlyList<LlmTool>? tools,
            CancellationToken cancellationToken,
            Func<LlmTokenUsage, Task>? onUsage,
            Func<Task>? onNativeToolCall,
            string errorMessageTemplate,
            string emptyResponseMessage,
            Action<HttpRequestMessage>? configureRequest = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string requestUrl = BuildRequestUrl(endpoint);
            var payload = BuildPayload(
                model,
                systemPrompt,
                userContent,
                outputLimit,
                reasoningEffort,
                attachments,
                tools,
                stream: false);

            using var request = CreateRequest(requestUrl, apiKey, payload, configureRequest);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(string.Format(
                    errorMessageTemplate,
                    response.StatusCode,
                    responseBody));
            }

            using var document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;
            await ReportUsageIfPresentAsync(root, onUsage);
            ThrowIfResponseTruncated(root);

            string? contentText = null;
            string? outputTextFallback = null;
            string? reasoningText = null;
            JsonElement? functionCall = null;

            if (root.TryGetProperty("output_text", out var outputText) &&
                outputText.ValueKind == JsonValueKind.String)
            {
                outputTextFallback = outputText.GetString();
            }

            if (root.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("type", out var typeProperty))
                    {
                        continue;
                    }

                    string itemType = typeProperty.GetString() ?? string.Empty;
                    if (itemType == "function_call")
                    {
                        functionCall ??= item;
                    }
                    else if (itemType == "message")
                    {
                        string? text = ReadOutputText(item);
                        if (!string.IsNullOrEmpty(text))
                        {
                            contentText = (contentText ?? string.Empty) + text;
                        }
                    }
                    else if (itemType == "reasoning")
                    {
                        string? summary = ReadReasoningSummary(item);
                        if (!string.IsNullOrEmpty(summary))
                        {
                            reasoningText = (reasoningText ?? string.Empty) + summary;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(contentText))
            {
                contentText = outputTextFallback;
            }

            if (functionCall.HasValue)
            {
                if (onNativeToolCall != null)
                {
                    await onNativeToolCall();
                }

                string toolName = functionCall.Value.TryGetProperty("name", out var nameProperty)
                    ? nameProperty.GetString() ?? string.Empty
                    : string.Empty;
                string toolArguments = functionCall.Value.TryGetProperty("arguments", out var argumentsProperty)
                    ? argumentsProperty.GetString() ?? string.Empty
                    : string.Empty;
                string toolCallText = LlmToolCallTextFormatter.FormatFunctionToolCall(toolName, toolArguments);

                if (!string.IsNullOrEmpty(contentText))
                {
                    return contentText.TrimEnd() + Environment.NewLine + Environment.NewLine + toolCallText;
                }

                return toolCallText;
            }

            if (!string.IsNullOrEmpty(contentText))
            {
                return contentText;
            }

            if (!string.IsNullOrEmpty(reasoningText))
            {
                return reasoningText;
            }

            return emptyResponseMessage;
        }

        public static async Task GenerateCompletionStreamAsync(
            HttpClient httpClient,
            string endpoint,
            string apiKey,
            string model,
            string systemPrompt,
            string userContent,
            int outputLimit,
            string? reasoningEffort,
            IReadOnlyList<LlmMessageAttachment>? attachments,
            IReadOnlyList<LlmTool>? tools,
            Func<string, Task> onChunk,
            CancellationToken cancellationToken,
            Func<string, Task>? onReasoning,
            Func<LlmTokenUsage, Task>? onUsage,
            Func<Task>? onNativeToolCall,
            string errorMessageTemplate,
            Action<HttpRequestMessage>? configureRequest = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string requestUrl = BuildRequestUrl(endpoint);
            var payload = BuildPayload(
                model,
                systemPrompt,
                userContent,
                outputLimit,
                reasoningEffort,
                attachments,
                tools,
                stream: true);

            using var request = CreateRequest(requestUrl, apiKey, payload, configureRequest);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(string.Format(
                    errorMessageTemplate,
                    response.StatusCode,
                    errorBody));
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new System.IO.StreamReader(stream);
            var toolAccumulator = new StreamToolCallAccumulator();
            bool hasToolCalls = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(
                    TimeSpan.FromSeconds(60),
                    cancellationToken);
                if (line == null)
                {
                    break;
                }

                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) ||
                    !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string data = trimmed.Substring(5).Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                try
                {
                    using var document = JsonDocument.Parse(data);
                    JsonElement root = document.RootElement;
                    string eventType = root.TryGetProperty("type", out var typeProperty)
                        ? typeProperty.GetString() ?? string.Empty
                        : string.Empty;

                    switch (eventType)
                    {
                        case "response.output_text.delta":
                            if (root.TryGetProperty("delta", out var textDelta) &&
                                textDelta.ValueKind == JsonValueKind.String)
                            {
                                string? text = textDelta.GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    await onChunk(text);
                                }
                            }
                            break;

                        case "response.reasoning_summary_text.delta":
                        case "response.reasoning_text.delta":
                            if (onReasoning != null &&
                                root.TryGetProperty("delta", out var reasoningDelta) &&
                                reasoningDelta.ValueKind == JsonValueKind.String)
                            {
                                string? reasoningText = reasoningDelta.GetString();
                                if (!string.IsNullOrEmpty(reasoningText))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    await onReasoning(reasoningText);
                                }
                            }
                            break;

                        case "response.output_item.added":
                            if (root.TryGetProperty("item", out var item) &&
                                item.ValueKind == JsonValueKind.Object &&
                                item.TryGetProperty("type", out var itemType) &&
                                itemType.GetString() == "function_call")
                            {
                                if (!hasToolCalls)
                                {
                                    hasToolCalls = true;
                                    if (onNativeToolCall != null)
                                    {
                                        await onNativeToolCall();
                                    }
                                }

                                string name = item.TryGetProperty("name", out var nameProperty)
                                    ? nameProperty.GetString() ?? string.Empty
                                    : string.Empty;
                                if (!string.IsNullOrEmpty(name))
                                {
                                    toolAccumulator.Name += name;
                                    if (!toolAccumulator.SentStartTag)
                                    {
                                        toolAccumulator.SentStartTag = true;
                                        await onChunk($"<tool_call>{{\"name\":{JsonSerializer.Serialize(name)}");
                                    }
                                    else
                                    {
                                        await onChunk(name);
                                    }
                                }
                            }
                            break;

                        case "response.function_call_arguments.delta":
                            if (root.TryGetProperty("delta", out var argumentsDelta) &&
                                argumentsDelta.ValueKind == JsonValueKind.String)
                            {
                                string argumentsChunk = argumentsDelta.GetString() ?? string.Empty;
                                if (!string.IsNullOrEmpty(argumentsChunk))
                                {
                                    toolAccumulator.Arguments.Append(argumentsChunk);
                                    if (!toolAccumulator.SentStartTag)
                                    {
                                        toolAccumulator.SentStartTag = true;
                                        await onChunk("<tool_call>{\"name\":\"\",\"arguments\":");
                                        toolAccumulator.SentArgumentsHeader = true;
                                    }
                                    else if (!toolAccumulator.SentArgumentsHeader)
                                    {
                                        toolAccumulator.SentArgumentsHeader = true;
                                        await onChunk("\",\"arguments\":");
                                    }

                                    await onChunk(argumentsChunk);
                                }
                            }
                            break;

                        default:
                            await ReportUsageIfPresentAsync(root, onUsage);
                            ThrowIfResponseTruncated(root);
                            break;
                    }
                }
                catch (JsonException)
                {
                }
            }

            if (hasToolCalls)
            {
                if (!toolAccumulator.SentStartTag)
                {
                    await onChunk("<tool_call>{\"name\":\"\",\"arguments\":{}");
                }
                else if (!toolAccumulator.SentArgumentsHeader)
                {
                    await onChunk("\",\"arguments\":{}");
                }

                await onChunk("}</tool_call>");
            }
        }

        public static string? GetReasoningEffort(string thinkingLevel)
        {
            string level = (thinkingLevel ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(level) || level == "default")
            {
                return null;
            }

            if (level == "xhigh" || level == "max")
            {
                return "high";
            }

            return level switch
            {
                "disabled" or "none" or "low" or "medium" or "high" => level,
                _ => null
            };
        }

        private static HttpRequestMessage CreateRequest(
            string requestUrl,
            string apiKey,
            Dictionary<string, object> payload,
            Action<HttpRequestMessage>? configureRequest)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            AddAuthorization(request, apiKey);
            configureRequest?.Invoke(request);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            return request;
        }

        private static Dictionary<string, object> BuildPayload(
            string model,
            string systemPrompt,
            string userContent,
            int outputLimit,
            string? reasoningEffort,
            IReadOnlyList<LlmMessageAttachment>? attachments,
            IReadOnlyList<LlmTool>? tools,
            bool stream)
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = model,
                ["instructions"] = systemPrompt,
                ["input"] = BuildInput(userContent, attachments)
            };

            if (outputLimit > 0)
            {
                payload["max_output_tokens"] = outputLimit;
            }

            if (stream)
            {
                payload["stream"] = true;
            }

            if (!string.IsNullOrWhiteSpace(reasoningEffort))
            {
                payload["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = reasoningEffort
                };
            }

            if (tools != null && tools.Count > 0)
            {
                var toolsList = new List<object>();
                foreach (var tool in tools)
                {
                    toolsList.Add(new
                    {
                        type = "function",
                        name = tool.Name,
                        description = tool.Description,
                        parameters = tool.Parameters
                    });
                }

                payload["tools"] = toolsList;
            }

            return payload;
        }

        private static object BuildInput(
            string userContent,
            IReadOnlyList<LlmMessageAttachment>? attachments)
        {
            var images = attachments?.Where(a => a.IsImage && !string.IsNullOrWhiteSpace(a.Base64Data)).ToList();
            if (images == null || images.Count == 0)
            {
                return userContent;
            }

            var parts = new List<object>
            {
                new { type = "input_text", text = userContent }
            };

            foreach (var image in images)
            {
                parts.Add(new
                {
                    type = "input_image",
                    image_url = $"data:{image.MimeType};base64,{image.Base64Data}"
                });
            }

            return new[]
            {
                new { role = "user", content = (object)parts }
            };
        }

        private static string BuildRequestUrl(string endpoint)
        {
            return string.IsNullOrWhiteSpace(endpoint)
                ? string.Empty
                : endpoint.Trim().TrimEnd('/') + "/responses";
        }

        private static void AddAuthorization(HttpRequestMessage request, string apiKey)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    NormalizeBearerCredential(apiKey));
            }
        }

        private static bool? ClassifyProbeResponse(HttpStatusCode statusCode, string responseBody)
        {
            if (statusCode == HttpStatusCode.NotFound ||
                statusCode == HttpStatusCode.MethodNotAllowed ||
                statusCode == HttpStatusCode.NotImplemented)
            {
                return false;
            }

            if ((int)statusCode >= 200 && (int)statusCode < 300)
            {
                return true;
            }

            if (statusCode == HttpStatusCode.BadRequest ||
                statusCode == HttpStatusCode.UnprocessableEntity ||
                statusCode == HttpStatusCode.UnsupportedMediaType)
            {
                return true;
            }

            // Authentication, throttling, and server errors do not tell us whether
            // the route exists. Do not cache those results.
            _ = responseBody;
            return null;
        }

        private static async Task ReportUsageIfPresentAsync(
            JsonElement root,
            Func<LlmTokenUsage, Task>? onUsage)
        {
            await LlmUsageReporter.TryReportUsageAsync(root, onUsage);
            if (root.TryGetProperty("response", out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                await LlmUsageReporter.TryReportUsageAsync(nested, onUsage);
            }
        }

        private static void ThrowIfResponseTruncated(JsonElement root)
        {
            JsonElement response = root;
            if (root.TryGetProperty("response", out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                response = nested;
            }

            if (response.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                status.GetString() == "incomplete" &&
                response.TryGetProperty("incomplete_details", out var details) &&
                details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty("reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String &&
                reason.GetString() == "max_output_tokens")
            {
                throw new ResponseTruncatedException();
            }
        }

        private static string? ReadOutputText(JsonElement item)
        {
            if (!item.TryGetProperty("content", out var content))
            {
                return null;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                string plainText = content.GetString() ?? string.Empty;
                return string.IsNullOrEmpty(plainText) ? null : plainText;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object ||
                    !part.TryGetProperty("type", out var typeProperty))
                {
                    continue;
                }

                string partType = typeProperty.GetString() ?? string.Empty;
                if ((partType == "output_text" || partType == "text") &&
                    part.TryGetProperty("text", out var textProperty) &&
                    textProperty.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textProperty.GetString());
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        private static string? ReadReasoningSummary(JsonElement item)
        {
            if (!item.TryGetProperty("summary", out var summary))
            {
                return null;
            }

            if (summary.ValueKind == JsonValueKind.String)
            {
                return summary.GetString();
            }

            if (summary.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var entry in summary.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("type", out var typeProperty) &&
                    typeProperty.GetString() == "summary_text" &&
                    entry.TryGetProperty("text", out var textProperty) &&
                    textProperty.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textProperty.GetString());
                }
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        private static string NormalizeBearerCredential(string credential)
        {
            string value = (credential ?? string.Empty).Trim();
            const string bearerPrefix = "Bearer ";
            if (value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return value.Substring(bearerPrefix.Length).Trim();
            }

            return value;
        }
    }
}
