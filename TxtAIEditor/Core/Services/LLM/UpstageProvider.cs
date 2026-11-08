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
    public class UpstageProvider : OpenAICompatibleProviderBase
    {
        private readonly ILocalizationService _localizationService;
        private readonly string _thinkingLevel;
        private readonly string _providerName;


        public UpstageProvider(ILocalizationService localizationService, string thinkingLevel = "", string providerName = "Upstage")
        {
            _localizationService = localizationService;
            _thinkingLevel = thinkingLevel ?? string.Empty;
            _providerName = providerName ?? "Upstage";
        }

        private Task<(int context, int output)> GetTokenLimitsAsync(string model, CancellationToken cancellationToken) =>
            GetCatalogTokenLimitsAsync(_providerName, model, cancellationToken);

        public override async Task<string> GenerateCompletionAsync(LlmCompletionRequest completion)
        {
            var endpoint = completion.Endpoint;
            var apiKey = completion.ApiKey;
            var model = completion.Model;
            var systemPrompt = completion.SystemPrompt;
            var userContent = completion.UserContent;
            var cancellationToken = completion.CancellationToken;
            var attachments = completion.Attachments;
            var tools = completion.Tools;
            var onUsage = completion.OnUsage;
            var onNativeToolCall = completion.OnNativeToolCall;
            var onApiType = completion.OnApiType;
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
                    _httpClient, completion, new LlmResponsesOptions
                    {
                        OutputLimit = outputLimit,
                        ReasoningEffort = GetReasoningEffort(model, _thinkingLevel),
                        ErrorMessageTemplate = _localizationService.GetString("UpstageErrorApiCallFailed", "Upstage API 호출 실패 ({0}): {1}"),
                        EmptyResponseMessage = _localizationService.GetString("LlmErrorEmptyResponse", "AI로부터 빈 응답을 수신했습니다."),
                    });
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
                        throw new LlmHttpException(response, string.Format(_localizationService.GetString("UpstageErrorApiCallFailed", "Upstage API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
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

        public override async Task GenerateCompletionStreamAsync(LlmCompletionRequest completion)
        {
            var cancellationToken = completion.CancellationToken;
            var onChunk = completion.OnChunk ?? throw new ArgumentException("A streaming callback is required.", nameof(completion));
            // Upstage does not support the streaming response path used by the editor.
            // Keep the provider interface contract by delivering the complete response
            // as one chunk after a normal, non-streaming request.
            string response = await GenerateCompletionAsync(completion);

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
            };
            LlmRequestTuning.SetTemperature(payloadDict, 0.5);

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


    }
}
