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
    public class LMStudioProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;

        private static readonly HttpClient _httpClient = new HttpClient();

        public LMStudioProvider(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, Func<Task>? onNativeToolCall = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException(_localizationService.GetString("LmStudioErrorNoModelSelected", "LM Studio 모델을 먼저 선택해 주십시오."));

            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:1234/v1" : endpoint.Trim();
            string requestUrl = baseEndpoint.TrimEnd('/') + "/chat/completions";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[]
                {
                    new { role = "system", content = (object)systemPrompt },
                    new { role = "user", content = BuildUserContent(userContent, attachments) }
                },
                ["temperature"] = 0.5
            };
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
                        throw new HttpRequestException(string.Format(_localizationService.GetString("LmStudioErrorApiCallFailed", "LM Studio API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
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
                            if (firstChoice.TryGetProperty("message", out var message))
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
                                    var firstToolCall = toolCalls[0];
                                    if (onNativeToolCall != null)
                                    {
                                        await onNativeToolCall();
                                    }
                                    return LlmToolCallTextFormatter.FormatAssistantResponseWithFunctionToolCall(contentText, firstToolCall);
                                }

                                if (!string.IsNullOrEmpty(contentText))
                                {
                                    return contentText;
                                }

                                if (message.TryGetProperty("reasoning_content", out var reasoningContent) &&
                                    reasoningContent.ValueKind == JsonValueKind.String)
                                {
                                    string? reasoningText = reasoningContent.GetString();
                                    if (!string.IsNullOrEmpty(reasoningText))
                                    {
                                        return reasoningText;
                                    }
                                }

                                if (message.TryGetProperty("reasoning", out var reasoning) &&
                                    reasoning.ValueKind == JsonValueKind.String)
                                {
                                    string? reasoningText = reasoning.GetString();
                                    if (!string.IsNullOrEmpty(reasoningText))
                                    {
                                        return reasoningText;
                                    }
                                }
                            }
                        }
                    }

                    return _localizationService.GetString("LmStudioErrorEmptyResponse", "LM Studio로부터 빈 응답을 수신했습니다.");
                }
            }
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, Func<Task>? onNativeToolCall = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException(_localizationService.GetString("LmStudioErrorNoModelSelected", "LM Studio 모델을 먼저 선택해 주십시오."));

            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:1234/v1" : endpoint.Trim();
            string requestUrl = baseEndpoint.TrimEnd('/') + "/chat/completions";

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
                        throw new HttpRequestException(string.Format(_localizationService.GetString("LmStudioErrorStreamCallFailed", "LM Studio API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
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

                            string data = line.Substring(6);
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
                                        if (firstChoice.TryGetProperty("delta", out var delta))
                                        {
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
                                                if (tc.TryGetProperty("function", out var function))
                                                {
                                                    if (function.TryGetProperty("name", out var nameProperty))
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

                                                    if (function.TryGetProperty("arguments", out var argumentsProperty))
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

                                            if (onReasoning != null)
                                            {
                                                string? reasoningText = null;
                                                if (delta.TryGetProperty("reasoning_content", out var reasoningContent) &&
                                                    reasoningContent.ValueKind == JsonValueKind.String)
                                                {
                                                    reasoningText = reasoningContent.GetString();
                                                }
                                                else if (delta.TryGetProperty("reasoning", out var reasoning) &&
                                                         reasoning.ValueKind == JsonValueKind.String)
                                                {
                                                    reasoningText = reasoning.GetString();
                                                }

                                                if (!string.IsNullOrEmpty(reasoningText))
                                                {
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    await onReasoning(reasoningText);
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
