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
    public class OpenAIProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;
        private readonly bool _isOAuth;
        private readonly string _thinkingLevel;
        private readonly string _providerName;

        private static readonly HttpClient _httpClient = new HttpClient();

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

        private async Task<(int context, int output)> GetTokenLimitsAsync(string model, CancellationToken cancellationToken)
        {
            var (context, output) = await ModelsDevCatalog.GetLimitsAsync(_providerName, model, cancellationToken);
            return (context, output > 0 ? output : 0);
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isLocalEndpoint = endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   endpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(apiKey) && !isLocalEndpoint)
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiCredential", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";

            var (contextLimit, outputLimit) = await GetTokenLimitsAsync(model, cancellationToken);
            outputLimit = LlmTokenBudget.GetSafeMaxOutputTokens(
                contextLimit,
                outputLimit,
                systemPrompt,
                userContent,
                attachments,
                tools);
            bool reasoning = IsReasoningModel(model);
            string tokenField = reasoning ? "max_completion_tokens" : "max_tokens";

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[]
                {
                    new { role = "system", content = (object)systemPrompt },
                    new { role = "user", content = BuildUserContent(userContent, attachments) }
                }
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

            if (!_isOAuth)
            {
                payloadDict["temperature"] = IsKimiModel(model) ? 1.0 : 0.5;
            }
            if (outputLimit > 0)
            {
                payloadDict[tokenField] = outputLimit;
            }
            if (HasThinking)
            {
                payloadDict["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = _thinkingLevel.ToLowerInvariant()
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
                        throw new HttpRequestException(string.Format(_localizationService.GetString("OpenAIErrorApiCallFailed", "OpenAI API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
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

                                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array && toolCalls.GetArrayLength() > 0)
                                {
                                    var firstToolCall = toolCalls[0];
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

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isLocalEndpoint = endpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   endpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(apiKey) && !isLocalEndpoint)
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiCredential", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";

            var (contextLimit, outputLimit) = await GetTokenLimitsAsync(model, cancellationToken);
            outputLimit = LlmTokenBudget.GetSafeMaxOutputTokens(
                contextLimit,
                outputLimit,
                systemPrompt,
                userContent,
                attachments,
                tools);
            bool reasoning = IsReasoningModel(model);
            string tokenField = reasoning ? "max_completion_tokens" : "max_tokens";

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

            if (!_isOAuth)
            {
                payloadDict["temperature"] = IsKimiModel(model) ? 1.0 : 0.5;
            }
            if (outputLimit > 0)
            {
                payloadDict[tokenField] = outputLimit;
            }
            if (HasThinking)
            {
                payloadDict["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = _thinkingLevel.ToLowerInvariant()
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
                        throw new HttpRequestException(string.Format(_localizationService.GetString("OpenAIErrorStreamCallFailed", "OpenAI API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
                    }

                    var toolAccumulator = new StreamToolCallAccumulator();
                    bool hasToolCalls = false;

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new System.IO.StreamReader(stream))
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
                                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                                    {
                                        var firstChoice = choices[0];
                                        if (firstChoice.TryGetProperty("delta", out var delta))
                                        {
                                            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array && toolCalls.GetArrayLength() > 0)
                                            {
                                                hasToolCalls = true;
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

        private static bool IsKimiModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            return model.Contains("kimi", StringComparison.OrdinalIgnoreCase) ||
                   model.Contains("moonshot", StringComparison.OrdinalIgnoreCase);
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
