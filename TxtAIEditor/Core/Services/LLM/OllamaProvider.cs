using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Core.Services.LLM
{
    public class OllamaProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;
        private readonly bool _isCloud;
        private readonly string _thinkingLevel;
        private readonly string _providerName;

        private static readonly HttpClient _httpClient = new HttpClient();

        public OllamaProvider(ILocalizationService localizationService, bool isCloud, string thinkingLevel = "", string providerName = "")
        {
            _localizationService = localizationService;
            _isCloud = isCloud;
            _thinkingLevel = thinkingLevel ?? string.Empty;
            _providerName = providerName ?? (isCloud ? "Ollama Cloud" : "Ollama");
        }

        private bool HasThinking =>
            !string.IsNullOrEmpty(_thinkingLevel) &&
            !_thinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !_thinkingLevel.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !_thinkingLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase);

        private string GetReasoningEffort()
        {
            string level = _thinkingLevel.ToLowerInvariant();
            return level == "xhigh" || level == "max" ? "high" : level;
        }

        private async Task<(int context, int output)> GetTokenLimitsAsync(string model, CancellationToken cancellationToken)
        {
            if (!_isCloud) return (0, 0);
            var (context, output) = await ModelsDevCatalog.GetLimitsAsync(_providerName, model, cancellationToken);
            return (context, output > 0 ? output : 0);
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, Func<Task>? onNativeToolCall = null)
        {
            var sb = new StringBuilder();
            await GenerateCompletionStreamAsync(endpoint, apiKey, model, systemPrompt, userContent, chunk =>
            {
                sb.Append(chunk);
                return Task.CompletedTask;
            }, cancellationToken, attachments, null, tools, onUsage, onNativeToolCall);
            return sb.ToString();
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, Func<Task>? onNativeToolCall = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string defaultNoModelError = _isCloud
                ? _localizationService.GetString("OllamaCloudErrorNoModelSelected", "Ollama Cloud 모델을 먼저 선택해 주십시오.")
                : _localizationService.GetString("OllamaErrorNoModelSelected", "Ollama 모델을 먼저 선택해 주십시오.");

            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException(defaultNoModelError);

            if (_isCloud && string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            if (_isCloud)
            {
                // Ollama Cloud (ollama.com) does not expose /v1/responses yet,
                // so it keeps the OpenAI-compatible Chat Completions endpoint.
                await StreamChatCompletionsAsync(endpoint, apiKey, model, systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning, tools, onUsage, onNativeToolCall);
                return;
            }

            // Local Ollama (v0.13.3+) supports the OpenAI Responses API (/v1/responses).
            await StreamResponsesAsync(endpoint, apiKey, model, systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning, tools, onUsage, onNativeToolCall);
        }

        private async Task StreamResponsesAsync(
            string endpoint,
            string apiKey,
            string model,
            string systemPrompt,
            string userContent,
            Func<string, Task> onChunk,
            CancellationToken cancellationToken,
            IReadOnlyList<LlmMessageAttachment>? attachments,
            Func<string, Task>? onReasoning,
            IReadOnlyList<LlmTool>? tools,
            Func<LlmTokenUsage, Task>? onUsage,
            Func<Task>? onNativeToolCall)
        {
            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434/v1" : endpoint.Trim();
            string requestUrl = baseEndpoint.TrimEnd('/') + "/responses";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["instructions"] = systemPrompt,
                ["input"] = BuildResponsesInput(userContent, attachments),
                ["temperature"] = 0.5,
                ["stream"] = true
            };
            AddResponsesReasoning(payloadDict);
            AddResponsesTools(payloadDict, tools);

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new HttpRequestException(string.Format(_localizationService.GetString("OllamaErrorStreamCallFailed", "Ollama API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        var toolAccumulator = new StreamToolCallAccumulator();
                        bool hasToolCalls = false;

                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                            if (line == null) break;
                            if (string.IsNullOrEmpty(line)) continue;
                            if (!line.StartsWith("data: ")) continue;

                            string data = line.Substring(6).Trim();
                            if (data == "[DONE]") break;

                            try
                            {
                                using (var doc = JsonDocument.Parse(data))
                                {
                                    var root = doc.RootElement;
                                    string eventType = root.TryGetProperty("type", out var typeProperty)
                                        ? typeProperty.GetString() ?? string.Empty
                                        : string.Empty;

                                    switch (eventType)
                                    {
                                        case "response.output_text.delta":
                                            {
                                                if (root.TryGetProperty("delta", out var deltaProperty) &&
                                                    deltaProperty.ValueKind == JsonValueKind.String)
                                                {
                                                    string? text = deltaProperty.GetString();
                                                    if (!string.IsNullOrEmpty(text))
                                                    {
                                                        cancellationToken.ThrowIfCancellationRequested();
                                                        await onChunk(text);
                                                    }
                                                }
                                                break;
                                            }
                                        case "response.reasoning_summary_text.delta":
                                        case "response.reasoning_text.delta":
                                            {
                                                if (onReasoning != null &&
                                                    root.TryGetProperty("delta", out var deltaProperty) &&
                                                    deltaProperty.ValueKind == JsonValueKind.String)
                                                {
                                                    string? reasoningText = deltaProperty.GetString();
                                                    if (!string.IsNullOrEmpty(reasoningText))
                                                    {
                                                        cancellationToken.ThrowIfCancellationRequested();
                                                        await onReasoning(reasoningText);
                                                    }
                                                }
                                                break;
                                            }
                                        case "response.output_item.added":
                                            {
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
                                            }
                                        case "response.function_call_arguments.delta":
                                            {
                                                if (root.TryGetProperty("delta", out var deltaProperty) &&
                                                    deltaProperty.ValueKind == JsonValueKind.String)
                                                {
                                                    string argumentsChunk = deltaProperty.GetString() ?? string.Empty;
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
                                            }
                                        default:
                                            await ReportUsageIfPresentAsync(root, onUsage);
                                            break;
                                    }
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
                }
            }
        }

        private async Task StreamChatCompletionsAsync(
            string endpoint,
            string apiKey,
            string model,
            string systemPrompt,
            string userContent,
            Func<string, Task> onChunk,
            CancellationToken cancellationToken,
            IReadOnlyList<LlmMessageAttachment>? attachments,
            Func<string, Task>? onReasoning,
            IReadOnlyList<LlmTool>? tools,
            Func<LlmTokenUsage, Task>? onUsage,
            Func<Task>? onNativeToolCall)
        {
            string defaultEndpoint = "https://ollama.com";
            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? defaultEndpoint : endpoint.Trim();
            if (baseEndpoint.Equals("https://ollama.com", StringComparison.OrdinalIgnoreCase))
            {
                baseEndpoint = "https://ollama.com/v1";
            }
            string requestUrl = baseEndpoint.TrimEnd('/') + "/chat/completions";

            var (contextLimit, outputLimit) = await GetTokenLimitsAsync(model, cancellationToken);
            outputLimit = LlmTokenBudget.GetSafeMaxOutputTokens(
                contextLimit,
                outputLimit,
                systemPrompt,
                userContent,
                attachments,
                tools);

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[]
                {
                    new { role = "system", content = (object)systemPrompt },
                    new { role = "user", content = BuildUserContent(userContent, attachments) }
                },
                ["temperature"] = 0.5,
                ["stream"] = true
            };
            if (outputLimit > 0)
            {
                payloadDict["max_tokens"] = outputLimit;
            }
            if (HasThinking)
            {
                payloadDict["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = GetReasoningEffort()
                };
            }
            else if (_thinkingLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                     _thinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                payloadDict["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = "none"
                };
            }
            payloadDict["stream_options"] = new Dictionary<string, object>
            {
                ["include_usage"] = true
            };
            AddChatCompletionsTools(payloadDict, tools);

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new HttpRequestException(string.Format(_localizationService.GetString("OllamaCloudErrorStreamCallFailed", "Ollama Cloud API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        var toolAccumulator = new StreamToolCallAccumulator();
                        bool hasToolCalls = false;

                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                            if (line == null) break;
                            if (string.IsNullOrEmpty(line)) continue;
                            if (!line.StartsWith("data: ")) continue;

                            string data = line.Substring(6).Trim();
                            if (data == "[DONE]") break;

                            try
                            {
                                using (var doc = JsonDocument.Parse(data))
                                {
                                    var root = doc.RootElement;
                                    await LlmUsageReporter.TryReportUsageAsync(root, onUsage);
                                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                                    {
                                        var firstChoice = choices[0];
                                        if (firstChoice.TryGetProperty("delta", out var delta))
                                        {
                                            if (onReasoning != null &&
                                                LlmReasoningContentReader.TryGetText(delta, out string reasoningText))
                                            {
                                                cancellationToken.ThrowIfCancellationRequested();
                                                await onReasoning(reasoningText);
                                            }

                                            if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                                                toolCalls.ValueKind == JsonValueKind.Array &&
                                                toolCalls.GetArrayLength() > 0)
                                            {
                                                if (!hasToolCalls)
                                                {
                                                    hasToolCalls = true;
                                                    if (onNativeToolCall != null)
                                                    {
                                                        await onNativeToolCall();
                                                    }
                                                }
                                                var tc = toolCalls[0];
                                                if (tc.TryGetProperty("function", out var func))
                                                {
                                                    if (func.TryGetProperty("name", out var nameProp))
                                                    {
                                                        string nameChunk = nameProp.GetString() ?? string.Empty;
                                                        if (!string.IsNullOrEmpty(nameChunk))
                                                        {
                                                            toolAccumulator.Name += nameChunk;
                                                            if (!toolAccumulator.SentStartTag)
                                                            {
                                                                toolAccumulator.SentStartTag = true;
                                                                await onChunk($"<tool_call>{{\"name\":\"{nameChunk}");
                                                            }
                                                            else
                                                            {
                                                                await onChunk(nameChunk);
                                                            }
                                                        }
                                                    }
                                                    if (func.TryGetProperty("arguments", out var argsProp))
                                                    {
                                                        string argsChunk = argsProp.GetString() ?? string.Empty;
                                                        if (!string.IsNullOrEmpty(argsChunk))
                                                        {
                                                            toolAccumulator.Arguments.Append(argsChunk);
                                                            if (!toolAccumulator.SentStartTag)
                                                            {
                                                                toolAccumulator.SentStartTag = true;
                                                                await onChunk($"<tool_call>{{\"name\":\"\",\"arguments\":");
                                                                toolAccumulator.SentArgumentsHeader = true;
                                                            }
                                                            else if (!toolAccumulator.SentArgumentsHeader)
                                                            {
                                                                toolAccumulator.SentArgumentsHeader = true;
                                                                await onChunk($"\",\"arguments\":");
                                                            }
                                                            await onChunk(argsChunk);
                                                        }
                                                    }
                                                }
                                            }
                                            else if (delta.TryGetProperty("content", out var content))
                                            {
                                                string? text = content.GetString();
                                                if (!string.IsNullOrEmpty(text))
                                                {
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    await onChunk(text);
                                                }
                                            }
                                        }
                                    }
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
                                await onChunk($"<tool_call>{{\"name\":\"\",\"arguments\":{{}}");
                            }
                            else if (!toolAccumulator.SentArgumentsHeader)
                            {
                                await onChunk($"\",\"arguments\":{{}}");
                            }
                            await onChunk("}</tool_call>");
                        }
                    }
                }
            }
        }

        private static async Task ReportUsageIfPresentAsync(JsonElement root, Func<LlmTokenUsage, Task>? onUsage)
        {
            await LlmUsageReporter.TryReportUsageAsync(root, onUsage);
            if (root.TryGetProperty("response", out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                await LlmUsageReporter.TryReportUsageAsync(nested, onUsage);
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

            var sb = new StringBuilder();
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
                    sb.Append(textProperty.GetString());
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string? ReadReasoningSummary(JsonElement item)
        {
            if (!item.TryGetProperty("summary", out var summary) ||
                summary.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var entry in summary.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("type", out var typeProperty) &&
                    typeProperty.GetString() == "summary_text" &&
                    entry.TryGetProperty("text", out var textProperty) &&
                    textProperty.ValueKind == JsonValueKind.String)
                {
                    sb.Append(textProperty.GetString());
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private void AddResponsesReasoning(Dictionary<string, object> payloadDict)
        {
            if (HasThinking)
            {
                payloadDict["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = GetReasoningEffort()
                };
            }
            else if (_thinkingLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                     _thinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                payloadDict["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = "none"
                };
            }
        }

        private static void AddResponsesTools(Dictionary<string, object> payloadDict, IReadOnlyList<LlmTool>? tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return;
            }

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

            payloadDict["tools"] = toolsList;
        }

        private static void AddChatCompletionsTools(Dictionary<string, object> payloadDict, IReadOnlyList<LlmTool>? tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return;
            }

            var toolsList = new List<object>();
            foreach (var tool in tools)
            {
                toolsList.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = tool.Parameters
                    }
                });
            }

            payloadDict["tools"] = toolsList;
        }

        private static object BuildResponsesInput(string userContent, IReadOnlyList<LlmMessageAttachment>? attachments)
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

        private static object BuildUserContent(string userContent, IReadOnlyList<LlmMessageAttachment>? attachments)
        {
            var images = attachments?.Where(a => a.IsImage && !string.IsNullOrWhiteSpace(a.Base64Data)).ToList();
            if (images == null || images.Count == 0)
            {
                return userContent;
            }

            var parts = new List<object>
            {
                new { type = "text", text = userContent }
            };

            foreach (var image in images)
            {
                parts.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:{image.MimeType};base64,{image.Base64Data}"
                    }
                });
            }

            return parts;
        }
    }
}
