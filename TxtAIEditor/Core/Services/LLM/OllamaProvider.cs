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
        private readonly string _providerName;

        private static readonly HttpClient _httpClient = new HttpClient();

        public OllamaProvider(ILocalizationService localizationService, bool isCloud, string providerName = "")
        {
            _localizationService = localizationService;
            _isCloud = isCloud;
            _providerName = providerName ?? (isCloud ? "Ollama Cloud" : "Ollama");
        }

        private async Task<(int context, int output)> GetTokenLimitsAsync(string model, CancellationToken cancellationToken)
        {
            if (!_isCloud) return (0, 0);
            var (context, output) = await ModelsDevCatalog.GetLimitsAsync(_providerName, model, cancellationToken);
            return (context, output > 0 ? output : 0);
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null)
        {
            var sb = new StringBuilder();
            await GenerateCompletionStreamAsync(endpoint, apiKey, model, systemPrompt, userContent, chunk =>
            {
                sb.Append(chunk);
                return Task.CompletedTask;
            }, cancellationToken, attachments, null, tools);
            return sb.ToString();
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string defaultNoModelError = _isCloud 
                ? _localizationService.GetString("OllamaCloudErrorNoModelSelected", "Ollama Cloud 모델을 먼저 선택해 주십시오.")
                : _localizationService.GetString("OllamaErrorNoModelSelected", "Ollama 모델을 먼저 선택해 주십시오.");

            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException(defaultNoModelError);

            if (_isCloud && string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            string defaultEndpoint = _isCloud ? "https://ollama.com" : "http://localhost:11434/v1";
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
            if (_isCloud && tools != null && tools.Count > 0)
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
                        string errorKey = _isCloud ? "OllamaCloudErrorStreamCallFailed" : "OllamaErrorStreamCallFailed";
                        string errorDefault = _isCloud 
                            ? "Ollama Cloud API 스트리밍 호출 실패 ({0}): {1}"
                            : "Ollama API 스트리밍 호출 실패 ({0}): {1}";
                        throw new HttpRequestException(string.Format(_localizationService.GetString(errorKey, errorDefault), response.StatusCode, errorBody));
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
                                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                                    {
                                        var firstChoice = choices[0];
                                        if (firstChoice.TryGetProperty("delta", out var delta))
                                        {
                                            if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                                                toolCalls.ValueKind == JsonValueKind.Array &&
                                                toolCalls.GetArrayLength() > 0)
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
