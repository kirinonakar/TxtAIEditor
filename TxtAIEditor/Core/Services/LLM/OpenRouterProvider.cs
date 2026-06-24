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
    public class OpenRouterProvider : ILLMProvider
    {
        private readonly ILocalizationService _localizationService;
        private readonly string _providerName;

        private static readonly HttpClient _httpClient = new HttpClient();

        public OpenRouterProvider(ILocalizationService localizationService, string providerName = "OpenRouter")
        {
            _localizationService = localizationService;
            _providerName = providerName ?? "OpenRouter";
        }

        private async Task<int> GetOutputLimitAsync(string model, CancellationToken cancellationToken)
        {
            var (context, output) = await ModelsDevCatalog.GetBestLimitsAsync(_providerName, model, cancellationToken);
            return output > 0 ? output : 0;
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
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

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

            if (outputLimit > 0)
            {
                payloadDict["max_tokens"] = outputLimit;
            }

            string jsonPayload = JsonSerializer.Serialize(payloadDict);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Add("User-Agent", "TxtAIEditor/1.0.0");
                request.Headers.Add("HTTP-Referer", "https://github.com/kirinonakar/TxtAIEditor");
                request.Headers.Add("X-Title", "TxtAIEditor");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new HttpRequestException(string.Format(_localizationService.GetString("OpenRouterErrorStreamCallFailed", "OpenRouter API 스트리밍 호출 실패 ({0}): {1}"), response.StatusCode, errorBody));
                    }

                    var toolAccumulator = new StreamToolCallAccumulator();
                    bool hasToolCalls = false;

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken);
                            if (line == null) break;
                            
                            string trimmed = line.Trim();
                            if (string.IsNullOrEmpty(trimmed)) continue;
                            if (!trimmed.StartsWith("data:")) continue;

                            string data = trimmed.Substring(5).Trim();
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
                                            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
                                            {
                                                hasToolCalls = true;
                                                var tc = toolCalls[0];
                                                if (tc.TryGetProperty("function", out var func))
                                                {
                                                    if (func.TryGetProperty("name", out var nameProp))
                                                    {
                                                        toolAccumulator.Name += nameProp.GetString() ?? string.Empty;
                                                    }
                                                    if (func.TryGetProperty("arguments", out var argsProp))
                                                    {
                                                        string argsChunk = argsProp.GetString() ?? string.Empty;
                                                        if (!string.IsNullOrEmpty(argsChunk))
                                                        {
                                                            toolAccumulator.Arguments.Append(argsChunk);
                                                            if (!toolAccumulator.SentHeader && !string.IsNullOrEmpty(toolAccumulator.Name))
                                                            {
                                                                toolAccumulator.SentHeader = true;
                                                                await onChunk($"<tool_call>{{\"name\":\"{toolAccumulator.Name}\",\"arguments\":");
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
                            if (!toolAccumulator.SentHeader)
                            {
                                await onChunk($"<tool_call>{{\"name\":\"{toolAccumulator.Name}\",\"arguments\":{{}}");
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
    }
}
