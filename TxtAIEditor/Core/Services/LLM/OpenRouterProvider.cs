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

        private static readonly HttpClient _httpClient = new HttpClient();

        public OpenRouterProvider(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null)
        {
            var sb = new StringBuilder();
            await GenerateCompletionStreamAsync(endpoint, apiKey, model, systemPrompt, userContent, chunk =>
            {
                sb.Append(chunk);
                return Task.CompletedTask;
            }, cancellationToken, attachments);
            return sb.ToString();
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException(_localizationService.GetString("LlmErrorInvalidApiKey", "API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오."));

            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";

            var payload = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "system", content = (object)systemPrompt },
                    new { role = "user", content = BuildUserContent(userContent, attachments) }
                },
                temperature = 0.5,
                stream = true
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
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
