using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    public class GeminiProvider : ILLMProvider
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private string BuildGeminiUrl(string baseUrl, string model, bool stream)
        {
            string url;
            if (baseUrl.Contains("/v1beta/models") || baseUrl.Contains("/v1/models"))
            {
                url = $"{baseUrl}/{model}:{(stream ? "streamGenerateContent" : "generateContent")}";
            }
            else if (baseUrl.Contains("/v1beta") || baseUrl.Contains("/v1"))
            {
                url = $"{baseUrl}/models/{model}:{(stream ? "streamGenerateContent" : "generateContent")}";
            }
            else
            {
                url = $"{baseUrl}/v1beta/models/{model}:{(stream ? "streamGenerateContent" : "generateContent")}";
            }
            if (stream) url += "?alt=sse";
            return url;
        }

        public async Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오.");

            string baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://generativelanguage.googleapis.com" : endpoint.TrimEnd('/');
            string requestUrl = BuildGeminiUrl(baseUrl, model, false);

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = userContent }
                        }
                    }
                },
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.5
                }
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Add("x-goog-api-key", apiKey);
                string jsonPayload = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"Google Gemini API 호출 실패 ({response.StatusCode}): {responseBody}");
                    }

                    using (var doc = JsonDocument.Parse(responseBody))
                    {
                        var root = doc.RootElement;
                        // Extract candidates[0].content.parts[0].text
                        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                        {
                            var firstCandidate = candidates[0];
                            if (firstCandidate.TryGetProperty("content", out var candidateContent) &&
                                candidateContent.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                            {
                                var firstPart = parts[0];
                                if (firstPart.TryGetProperty("text", out var text))
                                {
                                    return text.GetString() ?? string.Empty;
                                }
                            }
                        }
                    }
                    
                    return "Gemini AI로부터 빈 응답을 수신했습니다.";
                }
            }
        }

        public async Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("API Key가 유효하지 않습니다. 설정을 먼저 확인해 주십시오.");

            string baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://generativelanguage.googleapis.com" : endpoint.TrimEnd('/');
            string requestUrl = BuildGeminiUrl(baseUrl, model, true);

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = userContent }
                        }
                    }
                },
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.5
                }
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
            {
                request.Headers.Add("x-goog-api-key", apiKey);
                string jsonPayload = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new HttpRequestException($"Google Gemini API 스트리밍 호출 실패 ({response.StatusCode}): {errorBody}");
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

                            string data = line.Substring(6);

                            try
                            {
                                using (var doc = JsonDocument.Parse(data))
                                {
                                    var root = doc.RootElement;
                                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                                    {
                                        var firstCandidate = candidates[0];
                                        if (firstCandidate.TryGetProperty("content", out var candidateContent) &&
                                            candidateContent.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                                        {
                                            var firstPart = parts[0];
                                            if (firstPart.TryGetProperty("text", out var text))
                                            {
                                                string? chunk = text.GetString();
                                                if (!string.IsNullOrEmpty(chunk))
                                                {
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    await onChunk(chunk);
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
                    }
                }
            }
        }
    }
}
