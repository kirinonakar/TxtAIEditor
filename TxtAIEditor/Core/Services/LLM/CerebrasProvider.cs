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
    public class CerebrasProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;
        private readonly string _thinkingLevel;
        private readonly string _providerName;

        private static readonly HttpClient _httpClient = new HttpClient();

        public CerebrasProvider(ILocalizationService localizationService, string thinkingLevel = "", string providerName = "Cerebras")
        {
            _localizationService = localizationService;
            _thinkingLevel = thinkingLevel ?? string.Empty;
            _providerName = providerName ?? "Cerebras";
        }

        private async Task<(int context, int output)> GetTokenLimitsAsync(string model, CancellationToken cancellationToken)
        {
            var (context, output) = await ModelsDevCatalog.GetLimitsAsync(_providerName, model, cancellationToken);
            return (context, output > 0 ? output : 0);
        }

        public async Task<string> GenerateCompletionAsync(
            string endpoint,
            string apiKey,
            string model,
            string systemPrompt,
            string userContent,
            CancellationToken cancellationToken = default,
            IReadOnlyList<LlmMessageAttachment>? attachments = null,
            IReadOnlyList<LlmTool>? tools = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));
            }

            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";
            var payloadDict = await BuildPayloadAsync(model, systemPrompt, userContent, attachments, tools, stream: false, cancellationToken);

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeBearerCredential(apiKey));
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(string.Format(_localizationService.GetString("CerebrasErrorApiCallFailed", "Cerebras API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
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
                                    return LlmToolCallTextFormatter.FormatAssistantResponseWithFunctionToolCall(contentText, firstToolCall);
                                }

                                if (!string.IsNullOrEmpty(contentText))
                                {
                                    return contentText;
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

                            if (firstChoice.TryGetProperty("finish_reason", out var finishReason) &&
                                finishReason.ValueKind == JsonValueKind.String &&
                                finishReason.GetString() == "length")
                            {
                                throw new ResponseTruncatedException();
                            }
                        }
                    }

                    return _localizationService.GetString("LlmErrorEmptyResponse", "AI로부터 빈 응답을 수신했습니다.");
                }
            }
        }

        public async Task GenerateCompletionStreamAsync(
            string endpoint,
            string apiKey,
            string model,
            string systemPrompt,
            string userContent,
            Func<string, Task> onChunk,
            CancellationToken cancellationToken = default,
            IReadOnlyList<LlmMessageAttachment>? attachments = null,
            Func<string, Task>? onReasoning = null,
            IReadOnlyList<LlmTool>? tools = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));
            }

            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";
            var payloadDict = await BuildPayloadAsync(model, systemPrompt, userContent, attachments, tools, stream: true, cancellationToken);

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeBearerCredential(apiKey));
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new HttpRequestException(string.Format(_localizationService.GetString("CerebrasErrorStreamCallFailed", "Cerebras API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
                    }

                    var toolAccumulator = new StreamToolCallAccumulator();
                    bool hasToolCalls = false;
                    bool truncated = false;

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                            if (line == null)
                            {
                                break;
                            }

                            string trimmed = line.Trim();
                            if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
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
                                using (var doc = JsonDocument.Parse(data))
                                {
                                    var root = doc.RootElement;
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
                                                hasToolCalls = true;
                                                await AccumulateToolCallAsync(toolAccumulator, toolCalls[0], onChunk);
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
                                                if (delta.TryGetProperty("reasoning", out var reasoning) &&
                                                    reasoning.ValueKind == JsonValueKind.String)
                                                {
                                                    reasoningText = reasoning.GetString();
                                                }
                                                else if (delta.TryGetProperty("reasoning_content", out var reasoningContent) &&
                                                         reasoningContent.ValueKind == JsonValueKind.String)
                                                {
                                                    reasoningText = reasoningContent.GetString();
                                                }

                                                if (!string.IsNullOrEmpty(reasoningText))
                                                {
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    await onReasoning(reasoningText);
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
                                await onChunk($"<tool_call>{{\"name\":\"\",\"arguments\":{{}}");
                            }
                            else if (!toolAccumulator.SentArgumentsHeader)
                            {
                                await onChunk($"\",\"arguments\":{{}}");
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

        private async Task<Dictionary<string, object>> BuildPayloadAsync(
            string model,
            string systemPrompt,
            string userContent,
            IReadOnlyList<LlmMessageAttachment>? attachments,
            IReadOnlyList<LlmTool>? tools,
            bool stream,
            CancellationToken cancellationToken)
        {
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
                ["temperature"] = IsGemmaModel(model) ? 1.0 : 0.5
            };

            if (stream)
            {
                payloadDict["stream"] = true;
            }

            if (IsGemmaModel(model))
            {
                payloadDict["top_p"] = 0.95;
            }

            if (tools != null && tools.Count > 0)
            {
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

            if (outputLimit > 0)
            {
                payloadDict["max_completion_tokens"] = outputLimit;
            }

            string? reasoningEffort = GetReasoningEffort(model, _thinkingLevel);
            if (!string.IsNullOrEmpty(reasoningEffort))
            {
                payloadDict["reasoning_effort"] = reasoningEffort;
            }

            return payloadDict;
        }

        private static async Task AccumulateToolCallAsync(StreamToolCallAccumulator toolAccumulator, JsonElement toolCall, Func<string, Task> onChunk)
        {
            if (!toolCall.TryGetProperty("function", out var function))
            {
                return;
            }

            if (function.TryGetProperty("name", out var nameProp))
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

            if (function.TryGetProperty("arguments", out var argsProp))
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

        private static string? GetReasoningEffort(string model, string thinkingLevel)
        {
            string level = (thinkingLevel ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(level) || level == "default")
            {
                return null;
            }

            if (level == "disabled" || level == "none")
            {
                return IsGptOssModel(model) ? null : "none";
            }

            if (level == "xhigh")
            {
                return "high";
            }

            return level switch
            {
                "low" or "medium" or "high" => level,
                _ => null
            };
        }

        private static bool IsGptOssModel(string model)
        {
            return model.Equals("gpt-oss-120b", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGemmaModel(string model)
        {
            return model.Equals("gemma-4-31b", StringComparison.OrdinalIgnoreCase);
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
