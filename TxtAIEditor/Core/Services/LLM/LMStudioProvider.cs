using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    public class LMStudioProvider : ILLMProvider
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent)
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("LM Studio 모델을 먼저 선택해 주십시오.");

            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:1234/v1" : endpoint.Trim();
            string requestUrl = baseEndpoint.TrimEnd('/') + "/chat/completions";

            var payload = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userContent }
                },
                temperature = 0.5
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request))
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"LM Studio API 호출 실패 ({response.StatusCode}): {responseBody}");
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var firstChoice = choices[0];
                            if (firstChoice.TryGetProperty("message", out var message) &&
                                message.TryGetProperty("content", out var content))
                            {
                                return content.GetString() ?? string.Empty;
                            }
                        }
                    }

                    return "LM Studio로부터 빈 응답을 수신했습니다.";
                }
            }
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk)
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("LM Studio 모델을 먼저 선택해 주십시오.");

            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:1234/v1" : endpoint.Trim();
            string requestUrl = baseEndpoint.TrimEnd('/') + "/chat/completions";

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
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync();
                        throw new HttpRequestException($"LM Studio API 스트리밍 호출 실패 ({response.StatusCode}): {errorBody}");
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        while (true)
                        {
                            string? line = await reader.ReadLineAsync();
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
                                        if (firstChoice.TryGetProperty("delta", out var delta) &&
                                            delta.TryGetProperty("content", out var content))
                                        {
                                            string? text = content.GetString();
                                            if (!string.IsNullOrEmpty(text))
                                            {
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
