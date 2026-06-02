using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    public class OpenRouterProvider : ILLMProvider
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default)
        {
            var sb = new StringBuilder();
            await GenerateCompletionStreamAsync(endpoint, apiKey, model, systemPrompt, userContent, chunk =>
            {
                sb.Append(chunk);
                return Task.CompletedTask;
            }, cancellationToken);
            return sb.ToString();
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오.");

            string requestUrl = endpoint.TrimEnd('/') + "/chat/completions";

            var payload = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
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
                        throw new HttpRequestException($"OpenRouter API 스트리밍 호출 실패 ({response.StatusCode}): {errorBody}");
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
    }
}
