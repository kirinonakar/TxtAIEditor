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
    public class UpstageProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;
        private readonly string _thinkingLevel;
        private readonly string _providerName;

        private static readonly HttpClient _httpClient = new HttpClient();

        public UpstageProvider(ILocalizationService localizationService, string thinkingLevel = "", string providerName = "Upstage")
        {
            _localizationService = localizationService;
            _thinkingLevel = thinkingLevel ?? string.Empty;
            _providerName = providerName ?? "Upstage";
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
            IReadOnlyList<LlmTool>? tools = null,
            Func<LlmTokenUsage, Task>? onUsage = null,
            Func<Task>? onNativeToolCall = null,
            Func<string, Task>? onApiType = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));
            }

            var (contextLimit, outputLimit) = await GetTokenLimitsAsync(model, cancellationToken);
            outputLimit = LlmTokenBudget.GetSafeMaxOutputTokens(
                contextLimit,
                outputLimit,
                systemPrompt,
                userContent,
                attachments,
                tools);

            if (await LlmResponsesApiClient.SupportsAsync(
                    _httpClient,
                    endpoint,
                    apiKey,
                    model,
                    cancellationToken))
            {
                await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.Responses);
                return await LlmResponsesApiClient.GenerateCompletionAsync(
                    _httpClient,
                    endpoint,
                    apiKey,
                    model,
                    systemPrompt,
                    userContent,
                    outputLimit,
                    GetReasoningEffort(model, _thinkingLevel),
                    attachments,
                    tools,
                    cancellationToken,
                    onUsage,
                    onNativeToolCall,
                    _localizationService.GetString("UpstageErrorApiCallFailed", "Upstage API 호출 실패 ({0}): {1}"),
                    _localizationService.GetString("LlmErrorEmptyResponse", "AI로부터 빈 응답을 수신했습니다."));
            }

            await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.ChatCompletions);
            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";
            var payloadDict = await BuildPayloadAsync(model, systemPrompt, userContent, attachments, tools, cancellationToken);

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
                        throw new HttpRequestException(string.Format(_localizationService.GetString("UpstageErrorApiCallFailed", "Upstage API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        await LlmUsageReporter.TryReportUsageAsync(root, onUsage);
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
            IReadOnlyList<LlmTool>? tools = null,
            Func<LlmTokenUsage, Task>? onUsage = null,
            Func<Task>? onNativeToolCall = null,
            Func<string, Task>? onApiType = null)
        {
            // Upstage does not support the streaming response path used by the editor.
            // Keep the provider interface contract by delivering the complete response
            // as one chunk after a normal, non-streaming request.
            string response = await GenerateCompletionAsync(
                endpoint,
                apiKey,
                model,
                systemPrompt,
                userContent,
                cancellationToken,
                attachments,
                tools,
                onUsage,
                onNativeToolCall,
                onApiType);

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(response))
            {
                await onChunk(response);
            }
        }

        private async Task<Dictionary<string, object>> BuildPayloadAsync(
            string model,
            string systemPrompt,
            string userContent,
            IReadOnlyList<LlmMessageAttachment>? attachments,
            IReadOnlyList<LlmTool>? tools,
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
                ["temperature"] = 0.5
            };

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
                payloadDict["max_tokens"] = outputLimit;
            }

            string? reasoningEffort = GetReasoningEffort(model, _thinkingLevel);
            if (!string.IsNullOrEmpty(reasoningEffort))
            {
                payloadDict["reasoning_effort"] = reasoningEffort;
            }

            return payloadDict;
        }

        private static string? GetReasoningEffort(string model, string thinkingLevel)
        {
            if (model.Contains("solar-mini", StringComparison.OrdinalIgnoreCase))
            {
                // solar-mini does not accept reasoning_effort; sending it returns HTTP 400.
                return null;
            }

            string level = (thinkingLevel ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(level) || level == "default")
            {
                return null;
            }

            if (level == "disabled" || level == "none")
            {
                return "minimal";
            }

            if (level == "xhigh" || level == "max")
            {
                return "high";
            }

            return level switch
            {
                "low" or "medium" or "high" => level,
                _ => null
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
