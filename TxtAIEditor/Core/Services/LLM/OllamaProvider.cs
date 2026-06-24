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

        private async Task<int> GetOutputLimitAsync(string model, CancellationToken cancellationToken)
        {
            if (!_isCloud) return 0;
            var (context, output) = await ModelsDevCatalog.GetLimitsAsync(_providerName, model, cancellationToken);
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
                ["stream"] = true
            };
            if (outputLimit > 0)
            {
                payloadDict["max_tokens"] = outputLimit;
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
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string? line = await reader.ReadLineAsync(cancellationToken);
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
                                        if (firstChoice.TryGetProperty("delta", out var delta) &&
                                            delta.TryGetProperty("content", out var content))
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
                            catch (JsonException)
                            {
                            }
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
