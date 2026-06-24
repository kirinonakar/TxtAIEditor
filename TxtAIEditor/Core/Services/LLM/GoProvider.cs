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
    public class GoProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;

        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly string _thinkingLevel;
        private readonly string _providerName;

        public GoProvider(ILocalizationService localizationService, string thinkingLevel = "", string providerName = "OpenCode Go")
        {
            _localizationService = localizationService;
            _thinkingLevel = thinkingLevel ?? "";
            _providerName = providerName ?? "OpenCode Go";
        }

        private static int GetThinkingBudget(string level) => level.ToLowerInvariant() switch
        {
            "low" => 1024,
            "medium" => 4096,
            "high" => 16384,
            "xhigh" => 32768,
            _ => 0
        };

        private bool HasThinking => !string.IsNullOrEmpty(_thinkingLevel) && _thinkingLevel != "none";

        private async Task<int> GetOutputLimitAsync(string model, CancellationToken cancellationToken)
        {
            var (context, output) = await ModelsDevCatalog.GetLimitsAsync(_providerName, model, cancellationToken);
            if (output > 0) return output;

            int maxTokens = 8192;
            if (HasThinking)
            {
                int budget = GetThinkingBudget(_thinkingLevel);
                maxTokens = Math.Max(8192, Math.Min(budget + 8192, 32768));
            }
            return maxTokens;
        }

        private static bool IsDeepSeekOrGlm(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            return model.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ||
                   model.Contains("glm", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> _anthropicModels = new(StringComparer.OrdinalIgnoreCase)
        {
            "minimax-m3", "minimax-m2.7", "minimax-m2.5",
            "qwen3.7-max", "qwen3.7-plus", "qwen3.6-plus", "qwen3.5-plus",
            "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-opus-4-5", "claude-opus-4-1",
            "claude-sonnet-4-6", "claude-sonnet-4-5", "claude-sonnet-4",
            "claude-haiku-4-5", "claude-3-5-haiku",
        };

        private static bool IsAnthropicModel(string model) => _anthropicModels.Contains(model);

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            if (IsAnthropicModel(model))
            {
                return await GenerateAnthropicCompletionAsync(endpoint, apiKey, model, systemPrompt, userContent, cancellationToken, attachments);
            }

            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";

            int outputLimit = await GetOutputLimitAsync(model, cancellationToken);

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[]
                {
                    new { role = "system", content = (object)systemPrompt },
                    new { role = "user", content = BuildUserContent(userContent, attachments) }
                },
                ["temperature"] = IsKimiModel(model) ? 1.0 : 0.5,
                ["max_tokens"] = outputLimit
            };

            if (HasThinking)
            {
                string effort = _thinkingLevel.ToLowerInvariant();
                if (effort == "xhigh" && IsDeepSeekOrGlm(model))
                {
                    effort = "max";
                }
                payloadDict["reasoning_effort"] = effort;
            }

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(string.Format(_localizationService.GetString("GoErrorApiCallFailed", "OpenCode Go API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var firstChoice = choices[0];
                            if (firstChoice.TryGetProperty("message", out var message))
                            {
                                if (message.TryGetProperty("content", out var content) &&
                                    content.ValueKind == JsonValueKind.String)
                                {
                                    string? text = content.GetString();
                                    if (!string.IsNullOrEmpty(text)) return text;
                                }

                                if (message.TryGetProperty("reasoning_content", out var reasoning) &&
                                    reasoning.ValueKind == JsonValueKind.String)
                                {
                                    string? reasoningText = reasoning.GetString();
                                    if (!string.IsNullOrEmpty(reasoningText)) return reasoningText;
                                }

                                if (message.TryGetProperty("reasoning", out var reasoningAlias) &&
                                    reasoningAlias.ValueKind == JsonValueKind.String)
                                {
                                    string? reasoningText = reasoningAlias.GetString();
                                    if (!string.IsNullOrEmpty(reasoningText)) return reasoningText;
                                }
                            }

                            if (firstChoice.TryGetProperty("finish_reason", out var fr) &&
                                fr.ValueKind == JsonValueKind.String &&
                                fr.GetString() == "length")
                            {
                                throw new ResponseTruncatedException();
                            }
                        }
                    }

                    return _localizationService.GetString("LlmErrorEmptyResponse", "AI로부터 빈 응답을 수신했습니다.");
                }
            }
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            if (IsAnthropicModel(model))
            {
                await GenerateAnthropicCompletionStreamAsync(endpoint, apiKey, model, systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning);
                return;
            }

            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";

            int outputLimit = await GetOutputLimitAsync(model, cancellationToken);

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[]
                {
                    new { role = "system", content = (object)systemPrompt },
                    new { role = "user", content = BuildUserContent(userContent, attachments) }
                },
                ["temperature"] = 0.5,
                ["stream"] = true,
                ["max_tokens"] = outputLimit
            };

            if (HasThinking)
            {
                string effort = _thinkingLevel.ToLowerInvariant();
                if (effort == "xhigh" && IsDeepSeekOrGlm(model))
                {
                    effort = "max";
                }
                payloadDict["reasoning_effort"] = effort;
            }

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new HttpRequestException(string.Format(_localizationService.GetString("GoErrorStreamCallFailed", "OpenCode Go API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        bool truncated = false;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken);
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
                                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                                    {
                                        var firstChoice = choices[0];
                                        if (firstChoice.TryGetProperty("delta", out var delta))
                                        {
                                            if (delta.TryGetProperty("content", out var content) &&
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
                                                if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                                                    reasoningText = rc.GetString();
                                                else if (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                                                    reasoningText = r.GetString();

                                                if (!string.IsNullOrEmpty(reasoningText))
                                                {
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    await onReasoning(reasoningText);
                                                }
                                            }
                                        }

                                        if (firstChoice.TryGetProperty("finish_reason", out var fr) &&
                                            fr.ValueKind == JsonValueKind.String &&
                                            fr.GetString() == "length")
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

                        if (truncated) throw new ResponseTruncatedException();
                    }
                }
            }
        }

        private async Task<string> GenerateAnthropicCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken, IReadOnlyList<LlmMessageAttachment>? attachments)
        {
            string requestUrl = endpoint.TrimEnd('/') + "/messages";

            int outputLimit = await GetOutputLimitAsync(model, cancellationToken);
            if (outputLimit <= 0) outputLimit = 8192;

            var messagesList = new List<object>();
            var userMsg = new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = userContent
            };
            messagesList.Add(userMsg);

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["max_tokens"] = outputLimit,
                ["messages"] = messagesList
            };

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                payloadDict["system"] = systemPrompt;
            }

            if (HasThinking)
            {
                int budget = GetThinkingBudget(_thinkingLevel);
                if (budget > 0)
                {
                    if (budget >= outputLimit) budget = Math.Max(1024, outputLimit - 1024);
                    payloadDict["thinking"] = new Dictionary<string, object>
                    {
                        ["type"] = "enabled",
                        ["budget_tokens"] = budget
                    };
                }
            }

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(string.Format(_localizationService.GetString("GoErrorAnthropicApiCallFailed", "Anthropic API 호출 실패 ({0}): {1}"), response.StatusCode, responseBody));
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("content", out var content) && content.GetArrayLength() > 0)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (block.TryGetProperty("type", out var blockType) &&
                                    blockType.ValueKind == JsonValueKind.String &&
                                    blockType.GetString() == "text" &&
                                    block.TryGetProperty("text", out var text))
                                {
                                    string? textValue = text.GetString();
                                    if (!string.IsNullOrEmpty(textValue)) return textValue;
                                }
                            }

                            var firstBlock = content[0];
                            if (firstBlock.TryGetProperty("text", out var fallbackText))
                            {
                                return fallbackText.GetString() ?? string.Empty;
                            }
                        }
                    }

                    return _localizationService.GetString("LlmErrorEmptyResponse", "AI로부터 빈 응답을 수신했습니다.");
                }
            }
        }

        private async Task GenerateAnthropicCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken, IReadOnlyList<LlmMessageAttachment>? attachments, Func<string, Task>? onReasoning)
        {
            string requestUrl = endpoint.TrimEnd('/') + "/messages";

            int outputLimit = await GetOutputLimitAsync(model, cancellationToken);
            if (outputLimit <= 0) outputLimit = 8192;

            var messagesList = new List<object>();
            messagesList.Add(new { role = "user", content = userContent });

            var payloadDict = new Dictionary<string, object>
            {
                ["model"] = model,
                ["max_tokens"] = outputLimit,
                ["messages"] = messagesList,
                ["stream"] = true
            };

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                payloadDict["system"] = systemPrompt;
            }

            if (HasThinking)
            {
                int budget = GetThinkingBudget(_thinkingLevel);
                if (budget > 0)
                {
                    if (budget >= outputLimit) budget = Math.Max(1024, outputLimit - 1024);
                    payloadDict["thinking"] = new Dictionary<string, object>
                    {
                        ["type"] = "enabled",
                        ["budget_tokens"] = budget
                    };
                }
            }

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new HttpRequestException(string.Format(_localizationService.GetString("GoErrorAnthropicStreamCallFailed", "Anthropic API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        string? currentEvent = null;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken);
                            if (line == null) break;

                            if (line.StartsWith("event: "))
                            {
                                currentEvent = line.Substring(7);
                                continue;
                            }

                            if (line.StartsWith("data: "))
                            {
                                string data = line.Substring(6);
                                if (string.IsNullOrEmpty(data)) continue;

                                if (currentEvent == "content_block_delta")
                                {
                                    try
                                    {
                                        using (var doc = JsonDocument.Parse(data))
                                        {
                                            var root = doc.RootElement;
                                            if (root.TryGetProperty("delta", out var delta))
                                            {
                                                string? deltaType = delta.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String
                                                    ? dt.GetString()
                                                    : null;

                                                if (deltaType == "text_delta" &&
                                                    delta.TryGetProperty("text", out var text))
                                                {
                                                    string? chunk = text.GetString();
                                                    if (!string.IsNullOrEmpty(chunk))
                                                    {
                                                        cancellationToken.ThrowIfCancellationRequested();
                                                        await onChunk(chunk);
                                                    }
                                                }
                                                else if (deltaType == "thinking_delta" &&
                                                         delta.TryGetProperty("thinking", out var thinking) &&
                                                         onReasoning != null)
                                                {
                                                    string? chunk = thinking.GetString();
                                                    if (!string.IsNullOrEmpty(chunk))
                                                    {
                                                        cancellationToken.ThrowIfCancellationRequested();
                                                        await onReasoning(chunk);
                                                    }
                                                }
                                                else if (deltaType == null &&
                                                         delta.TryGetProperty("text", out var fallbackText))
                                                {
                                                    string? chunk = fallbackText.GetString();
                                                    if (!string.IsNullOrEmpty(chunk))
                                                    {
                                                        cancellationToken.ThrowIfCancellationRequested();
                                                        await onChunk(chunk);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (JsonException) { }
                                }
                                continue;
                            }
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
    }
}
