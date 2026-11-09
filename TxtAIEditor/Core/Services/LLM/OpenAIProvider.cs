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
    public class OpenAIProvider : OpenAICompatibleProviderBase
    {
        private readonly ILocalizationService _localizationService;
        private readonly bool _isOAuth;
        private readonly string _thinkingLevel;
        private readonly string _providerName;


        public OpenAIProvider(ILocalizationService localizationService, bool isOAuth = false, string thinkingLevel = "", string providerName = "OpenAI")
        {
            _localizationService = localizationService;
            _isOAuth = isOAuth;
            _thinkingLevel = thinkingLevel ?? "";
            _providerName = providerName ?? "OpenAI";
        }

        private static bool IsReasoningModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string m = model.ToLowerInvariant();
            return m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") || m.StartsWith("o5");
        }

        private bool HasThinking => !string.IsNullOrEmpty(_thinkingLevel) &&
                                    !_thinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                                    !_thinkingLevel.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                                    !_thinkingLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase);

        private bool IsOpenAIService =>
            _providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            _providerName.Equals("OpenAI OAuth", StringComparison.OrdinalIgnoreCase) ||
            _providerName.Equals("OpenAIOAuth", StringComparison.OrdinalIgnoreCase);

        private bool IsExaone => SettingsLlmModelCatalog.IsExaoneProvider(_providerName);

        private bool ForcesChatCompletions =>
            IsExaone || _providerName.Equals("Custom", StringComparison.OrdinalIgnoreCase);

        private Task<(int context, int output)> GetTokenLimitsAsync(string model, CancellationToken cancellationToken) =>
            IsExaone
                ? Task.FromResult(ModelsDevCatalog.GetCachedLimits(_providerName, model))
                : GetCatalogTokenLimitsAsync(_providerName, model, cancellationToken);

        public override async Task<string> GenerateCompletionAsync(LlmCompletionRequest completion)
        {
            var endpoint = IsExaone ? SettingsLlmModelCatalog.ExaoneEndpoint : completion.Endpoint;
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
            bool isLocalEndpoint = endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   endpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(apiKey) && !isLocalEndpoint)
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiCredential", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));
            ValidateAttachments(attachments);

            var (contextLimit, outputLimit) = await GetTokenLimitsAsync(model, cancellationToken);
            outputLimit = LlmTokenBudget.GetSafeMaxOutputTokens(
                contextLimit,
                outputLimit,
                systemPrompt,
                userContent,
                attachments,
                tools);

            if (!ForcesChatCompletions &&
                await LlmResponsesApiClient.SupportsAsync(
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
                        ReasoningEffort = GetResponsesReasoningEffort(model),
                        ErrorMessageTemplate = _localizationService.GetString("OpenAIErrorApiCallFailed", "OpenAI API 호출 실패 ({0}): {1}"),
                        EmptyResponseMessage = _localizationService.GetString("LlmErrorEmptyResponse", "AI로부터 빈 응답을 수신했습니다."),
                        AllowExplicitPromptCaching = !_isOAuth,
                    });
            }

            await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.ChatCompletions);
            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[]
                {
                    new { role = "system", content = (object)systemPrompt },
                    new { role = "user", content = BuildUserContent(userContent, attachments) }
                }
            };

            if (!isLocalEndpoint && IsOpenAIService)
            {
                payloadDict["prompt_cache_key"] = LlmResponsesApiClient.BuildPromptCacheKey(
                    model,
                    systemPrompt,
                    userContent);
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

            ApplyChatTuning(payloadDict, model, outputLimit);

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeBearerCredential(apiKey));
                }
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new LlmHttpException(response, string.Format(GetChatErrorTemplate(stream: false), response.StatusCode, responseBody));
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        await LlmUsageReporter.TryReportUsageAsync(root, onUsage);
                        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                        {
                            var firstChoice = choices[0];
                            if (IsExaone && HasReachedTokenLimit(firstChoice))
                            {
                                throw new ResponseTruncatedException();
                            }
                            if (firstChoice.TryGetProperty("message", out var message))
                            {
                                string? contentText = null;
                                if (message.TryGetProperty("content", out var content) &&
                                    content.ValueKind == JsonValueKind.String)
                                {
                                    contentText = content.GetString();
                                }

                                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array && toolCalls.GetArrayLength() > 0)
                                {
                                    var firstToolCall = toolCalls[0];
                                    if (onNativeToolCall != null)
                                    {
                                        await onNativeToolCall();
                                    }
                                    return LlmToolCallTextFormatter.FormatAssistantResponseWithFunctionToolCall(contentText, firstToolCall);
                                }
                                if (contentText != null)
                                {
                                    return contentText;
                                }
                            }
                        }
                    }
                    
                    return _localizationService.GetString("LlmErrorEmptyResponse", "AI로부터 빈 응답을 수신했습니다.");
                }
            }
        }

        public override async Task GenerateCompletionStreamAsync(LlmCompletionRequest completion)
        {
            var endpoint = IsExaone ? SettingsLlmModelCatalog.ExaoneEndpoint : completion.Endpoint;
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
            var onReasoning = completion.OnReasoning;
            var onChunk = completion.OnChunk ?? throw new ArgumentException("A streaming callback is required.", nameof(completion));
            cancellationToken.ThrowIfCancellationRequested();
            bool isLocalEndpoint = endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   endpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(apiKey) && !isLocalEndpoint)
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiCredential", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));
            ValidateAttachments(attachments);

            var (contextLimit, outputLimit) = await GetTokenLimitsAsync(model, cancellationToken);
            outputLimit = LlmTokenBudget.GetSafeMaxOutputTokens(
                contextLimit,
                outputLimit,
                systemPrompt,
                userContent,
                attachments,
                tools);

            if (!ForcesChatCompletions &&
                await LlmResponsesApiClient.SupportsAsync(
                    _httpClient,
                    endpoint,
                    apiKey,
                    model,
                    cancellationToken))
            {
                await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.Responses);
                await LlmResponsesApiClient.GenerateCompletionStreamAsync(
                    _httpClient, completion, new LlmResponsesOptions
                    {
                        OutputLimit = outputLimit,
                        ReasoningEffort = GetResponsesReasoningEffort(model),
                        ErrorMessageTemplate = _localizationService.GetString("OpenAIErrorStreamCallFailed", "OpenAI API 스트리밍 호출 실패 ({0}): {1}"),
                        AllowExplicitPromptCaching = !_isOAuth,
                    });
                return;
            }

            await LlmApiTypeReporter.ReportAsync(onApiType, LlmApiTypes.ChatCompletions);
            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[]
                {
                    new { role = "system", content = (object)systemPrompt },
                    new { role = "user", content = BuildUserContent(userContent, attachments) }
                },
                ["stream"] = true
            };

            if (!isLocalEndpoint && IsOpenAIService)
            {
                payloadDict["prompt_cache_key"] = LlmResponsesApiClient.BuildPromptCacheKey(
                    model,
                    systemPrompt,
                    userContent);
            }

            if (!isLocalEndpoint)
            {
                payloadDict["stream_options"] = new Dictionary<string, object>
                {
                    ["include_usage"] = true
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

            ApplyChatTuning(payloadDict, model, outputLimit);

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NormalizeBearerCredential(apiKey));
                }
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new LlmHttpException(response, string.Format(GetChatErrorTemplate(stream: true), response.StatusCode, errorBody));
                    }

                    var toolAccumulator = new StreamToolCallAccumulator();
                    bool hasToolCalls = false;

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new LlmSseReader(stream))
                    {
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
                                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
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

                                            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array && toolCalls.GetArrayLength() > 0)
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

        private void ValidateAttachments(IReadOnlyList<LlmMessageAttachment>? attachments)
        {
            if (IsExaone && attachments?.Any(a => a.IsImage && !string.IsNullOrWhiteSpace(a.Base64Data)) == true)
            {
                throw new LlmRequestException(_localizationService.GetString(
                    "ExaoneErrorImageUnsupported", "K-EXAONE 2.5는 텍스트 입력만 지원합니다."));
            }
        }

        private static bool HasReachedTokenLimit(JsonElement choice) =>
            choice.TryGetProperty("finish_reason", out var reason) &&
            reason.ValueKind == JsonValueKind.String &&
            reason.ValueEquals("length");

        private string GetChatErrorTemplate(bool stream)
        {
            if (IsExaone)
            {
                return _localizationService.GetString("ExaoneErrorApiCallFailed", "EXAONE API 호출 실패 ({0}): {1}");
            }

            return stream
                ? _localizationService.GetString("OpenAIErrorStreamCallFailed", "OpenAI API 스트리밍 호출 실패 ({0}): {1}")
                : _localizationService.GetString("OpenAIErrorApiCallFailed", "OpenAI API 호출 실패 ({0}): {1}");
        }

        private void ApplyChatTuning(Dictionary<string, object> payload, string model, int outputLimit)
        {
            if (IsExaone)
            {
                LlmRequestTuning.SetTemperature(payload, 1.0);
                payload["top_p"] = 0.95;
                if (outputLimit > 0)
                {
                    payload["max_tokens"] = outputLimit;
                }

                string? effort = _thinkingLevel.Trim().ToLowerInvariant() switch
                {
                    "none" or "disabled" or "low" => "low",
                    "medium" or "high" => "high",
                    "xhigh" or "max" => "max",
                    _ => null
                };
                if (effort != null)
                {
                    payload["reasoning_effort"] = effort;
                }
                return;
            }

            if (!_isOAuth)
            {
                LlmRequestTuning.SetTemperature(payload, IsKimiModel(model) ? 1.0 : 0.5);
            }
            if (outputLimit > 0)
            {
                string tokenField = IsReasoningModel(model) ? "max_completion_tokens" : "max_tokens";
                payload[tokenField] = outputLimit;
            }
            if (HasThinking)
            {
                payload["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = LlmThinkingLevelMapper.MapEffort(model, _thinkingLevel)
                };
            }
            else if (_thinkingLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                     _thinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                payload["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = "none"
                };
            }
        }

        private static bool IsKimiModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            return model.Contains("kimi", StringComparison.OrdinalIgnoreCase) ||
                   model.Contains("moonshot", StringComparison.OrdinalIgnoreCase);
        }

        private string? GetResponsesReasoningEffort(string model)
        {
            if (HasThinking)
            {
                string effort = LlmThinkingLevelMapper.MapEffort(model, _thinkingLevel);
                return effort.Equals("xhigh", StringComparison.OrdinalIgnoreCase) ||
                       effort.Equals("max", StringComparison.OrdinalIgnoreCase)
                    ? "high"
                    : effort;
            }

            return _thinkingLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                   _thinkingLevel.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? "none"
                : null;
        }


    }
}
