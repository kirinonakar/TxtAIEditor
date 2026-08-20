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
                payloadDict["enable_thinking"] = true;
                payloadDict["reasoning_effort"] = GetReasoningEffort();
            }
            else if (_thinkingLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                     _thinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                payloadDict["enable_thinking"] = false;
                payloadDict["reasoning_effort"] = "none";
            }
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, Func<Task>? onNativeToolCall = null, Func<string, Task>? onApiType = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException(_localizationService.GetString("UnslothErrorNoModelSelected", "Unsloth Desktop 모델을 먼저 선택해 주십시오."));

            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:8888/v1" : endpoint.Trim();
            await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.ChatCompletions);
            string requestUrl = baseEndpoint.TrimEnd('/') + "/chat/completions";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = BuildMessages(systemPrompt, userContent, attachments),
                ["temperature"] = 0.5,
                ["stream"] = false
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
                        await LlmUsageReporter.TryReportUsageAsync(root, onUsage);

                        if (root.TryGetProperty("choices", out var choices) &&
                            choices.ValueKind == JsonValueKind.Array &&
                            choices.GetArrayLength() > 0)
                        {
                            var firstChoice = choices[0];
                            if (firstChoice.TryGetProperty("message", out var message) &&
                                message.ValueKind == JsonValueKind.Object)
                            {
                                string? contentText = null;
                                if (message.TryGetProperty("content", out var content) &&
                                    content.ValueKind == JsonValueKind.String)
                                {
                                    contentText = content.GetString();
                                }

                                if (message.TryGetProperty("tool_calls", out var toolCalls) &&
                                    toolCalls.ValueKind == JsonValueKind.Array &&
                                    toolCalls.GetArrayLength() > 0)
                                {
                                    if (onNativeToolCall != null)
                                    {
                                        await onNativeToolCall();
                                    }

                                    return LlmToolCallTextFormatter.FormatAssistantResponseWithFunctionToolCall(
                                        contentText,
                                        toolCalls[0]);
                                }

                                if (!string.IsNullOrEmpty(contentText))
                                {
                                    return contentText;
                                }

                                if (LlmReasoningContentReader.TryGetText(message, out string reasoningText))
                                {
                                    return reasoningText;
                                }
                            }

                            if (firstChoice.TryGetProperty("finish_reason", out var finishReason) &&
                                finishReason.ValueKind == JsonValueKind.String &&
                                finishReason.GetString() == "length")
                            {
                                throw new ResponseTruncatedException();
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
            await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.ChatCompletions);
            string requestUrl = baseEndpoint.TrimEnd('/') + "/chat/completions";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = BuildMessages(systemPrompt, userContent, attachments),
                ["temperature"] = 0.5,
                ["stream"] = true,
                ["stream_options"] = new Dictionary<string, object>
                {
                    ["include_usage"] = true
                }
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
                        bool truncated = false;

                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                            if (line == null) break;
                            string trimmed = line.Trim();
                            if (string.IsNullOrEmpty(trimmed) ||
                                !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string data = trimmed.Substring(5).Trim();
                            if (data == "[DONE]") break;

                            try
                            {
                                using (var doc = JsonDocument.Parse(data))
                                {
                                    var root = doc.RootElement;
                                    await LlmUsageReporter.TryReportUsageAsync(root, onUsage);
                                    if (root.TryGetProperty("choices", out var choices) &&
                                        choices.ValueKind == JsonValueKind.Array &&
                                        choices.GetArrayLength() > 0)
                                    {
                                        var firstChoice = choices[0];
                                        if (firstChoice.TryGetProperty("delta", out var delta) &&
                                            delta.ValueKind == JsonValueKind.Object)
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

                                                await AccumulateToolCallAsync(
                                                    toolAccumulator,
                                                    toolCalls[0],
                                                    onChunk);
                                            }
                                            else if (delta.TryGetProperty("content", out var content) &&
                                                     content.ValueKind == JsonValueKind.String)
                                            {
                                                string? text = content.GetString();
                                                if (!string.IsNullOrEmpty(text))
                                                {
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    await onChunk(text);
                                                }
                                            }
                                        }

                                        if (firstChoice.TryGetProperty("finish_reason", out var finishReason) &&
                                            finishReason.ValueKind == JsonValueKind.String &&
                                            finishReason.GetString() == "length")
                                        {
                                            truncated = true;
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
                                await onChunk("<tool_call>{\"name\":\"\",\"arguments\":{}");
                            }
                            else if (!toolAccumulator.SentArgumentsHeader)
                            {
                                await onChunk("\",\"arguments\":{}");
                            }

                            await onChunk("}</tool_call>");
                        }

                        if (truncated)
                        {
                            throw new ResponseTruncatedException();
                        }
                    }
                }
            }
        }

        private static async Task AccumulateToolCallAsync(
            StreamToolCallAccumulator toolAccumulator,
            JsonElement toolCall,
            Func<string, Task> onChunk)
        {
            if (!toolCall.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (function.TryGetProperty("name", out var nameProperty) &&
                nameProperty.ValueKind == JsonValueKind.String)
            {
                string nameChunk = nameProperty.GetString() ?? string.Empty;
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

            if (function.TryGetProperty("arguments", out var argumentsProperty) &&
                argumentsProperty.ValueKind == JsonValueKind.String)
            {
                string argumentsChunk = argumentsProperty.GetString() ?? string.Empty;
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

        private static object[] BuildMessages(
            string systemPrompt,
            string userContent,
            IReadOnlyList<LlmMessageAttachment>? attachments)
        {
            return new[]
            {
                new { role = "system", content = (object)systemPrompt },
                new { role = "user", content = BuildUserContent(userContent, attachments) }
            };
        }

        private static object BuildUserContent(
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
