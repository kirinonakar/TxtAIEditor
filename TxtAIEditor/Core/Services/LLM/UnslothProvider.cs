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
    public class UnslothProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;
        private readonly string _thinkingLevel;

        private static readonly HttpClient _httpClient = new HttpClient();

        public UnslothProvider(ILocalizationService localizationService, string thinkingLevel = "")
        {
            _localizationService = localizationService;
            _thinkingLevel = thinkingLevel ?? string.Empty;
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

        private void AddReasoning(Dictionary<string, object> payloadDict)
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
                payloadDict["enable_thinking"] = false;
                payloadDict["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = "none"
                };
            }
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, Func<Task>? onNativeToolCall = null, Func<string, Task>? onApiType = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException(_localizationService.GetString("UnslothErrorNoModelSelected", "Unsloth Desktop 모델을 먼저 선택해 주십시오."));

            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:8888/v1" : endpoint.Trim();
            await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.Responses);
            string requestUrl = baseEndpoint.TrimEnd('/') + "/responses";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["instructions"] = systemPrompt,
                ["input"] = BuildInput(userContent, attachments),
                ["temperature"] = 0.5
            };
            AddReasoning(payloadDict);
            AddTools(payloadDict, tools);

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(string.Format(_localizationService.GetString("UnslothErrorApiCallFailed", "Unsloth Desktop API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        await ReportUsageIfPresentAsync(root, onUsage);

                        if (root.TryGetProperty("output", out var output) &&
                            output.ValueKind == JsonValueKind.Array)
                        {
                            string? contentText = null;
                            string? reasoningText = null;
                            JsonElement? functionCall = null;

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
                                    functionCall = item;
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
                        }
                    }

                    return _localizationService.GetString("UnslothErrorEmptyResponse", "Unsloth Desktop로부터 빈 응답을 수신했습니다.");
                }
            }
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, Func<Task>? onNativeToolCall = null, Func<string, Task>? onApiType = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException(_localizationService.GetString("UnslothErrorNoModelSelected", "Unsloth Desktop 모델을 먼저 선택해 주십시오."));

            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:8888/v1" : endpoint.Trim();
            await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.Responses);
            string requestUrl = baseEndpoint.TrimEnd('/') + "/responses";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["instructions"] = systemPrompt,
                ["input"] = BuildInput(userContent, attachments),
                ["temperature"] = 0.5,
                ["stream"] = true
            };
            AddReasoning(payloadDict);
            AddTools(payloadDict, tools);

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
                        throw new HttpRequestException(string.Format(_localizationService.GetString("UnslothErrorStreamCallFailed", "Unsloth Desktop API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
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

        private static void AddTools(Dictionary<string, object> payloadDict, IReadOnlyList<LlmTool>? tools)
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

        private static object BuildInput(string userContent, IReadOnlyList<LlmMessageAttachment>? attachments)
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
    }
}
