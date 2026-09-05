using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsLlmModelFetcher
    {
        public static async Task<IReadOnlyList<string>> FetchModelsAsync(string endpoint, string? apiKey = null)
        {
            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434/v1" : endpoint.Trim();
            if (baseEndpoint.Equals("https://ollama.com", StringComparison.OrdinalIgnoreCase))
            {
                baseEndpoint = "https://ollama.com/v1";
            }

            if (IsGoogleEndpoint(baseEndpoint))
            {
                return await FetchGoogleModelsAsync(baseEndpoint, apiKey);
            }

            string requestUrl = baseEndpoint.TrimEnd('/') + "/models";

            var models = new List<string>();

            // Try standard /v1/models first (OpenAI compatible endpoint supported by modern Ollama)
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    }
                    using (var response = await client.GetAsync(requestUrl))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            string responseBody = await response.Content.ReadAsStringAsync();
                            using (var doc = JsonDocument.Parse(responseBody))
                            {
                                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in data.EnumerateArray())
                                    {
                                        if (item.TryGetProperty("id", out var idElement))
                                        {
                                            string? id = idElement.GetString();
                                            if (!string.IsNullOrWhiteSpace(id))
                                            {
                                                models.Add(id);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore and fall through to fallback
            }

            if (models.Count > 0)
            {
                return models;
            }

            // Fallback: Try Ollama's native direct /api/tags endpoint.
            // If baseEndpoint ends with /v1, we strip it to hit the root domain /api/tags
            string tagsUrl = baseEndpoint;
            if (tagsUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                tagsUrl = tagsUrl.Substring(0, tagsUrl.Length - 3).TrimEnd('/') + "/api/tags";
            }
            else if (tagsUrl.EndsWith("/v1/", StringComparison.OrdinalIgnoreCase))
            {
                tagsUrl = tagsUrl.Substring(0, tagsUrl.Length - 4).TrimEnd('/') + "/api/tags";
            }
            else
            {
                tagsUrl = tagsUrl.TrimEnd('/') + "/api/tags";
            }

            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    }
                    using (var response = await client.GetAsync(tagsUrl))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            string responseBody = await response.Content.ReadAsStringAsync();
                            using (var doc = JsonDocument.Parse(responseBody))
                            {
                                if (doc.RootElement.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in modelsArray.EnumerateArray())
                                    {
                                        if (item.TryGetProperty("name", out var nameElement))
                                        {
                                            string? name = nameElement.GetString();
                                            if (!string.IsNullOrWhiteSpace(name))
                                            {
                                                models.Add(name);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore and let standard exception throw if we still have no models
            }

            if (models.Count == 0)
            {
                // Trigger original request error details so caller receives detailed HTTP exception
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    }
                    using (var response = await client.GetAsync(requestUrl))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"모델 목록 요청 실패 ({response.StatusCode}): {responseBody}");
                        }
                    }
                }
            }

            return models;
        }

        private static bool IsGoogleEndpoint(string endpoint)
        {
            return endpoint.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
        }

        // Google (Gemini) API lists models at /v1beta/models with the API key in the
        // x-goog-api-key header, names prefixed with "models/", and paginated results.
        private static async Task<IReadOnlyList<string>> FetchGoogleModelsAsync(string baseEndpoint, string? apiKey)
        {
            string root = baseEndpoint.TrimEnd('/');
            foreach (string suffix in new[] { "/v1beta", "/v1alpha", "/v1" })
            {
                if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    root = root.Substring(0, root.Length - suffix.Length).TrimEnd('/');
                    break;
                }
            }

            var models = new List<string>();
            string? pageToken = null;

            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                if (!string.IsNullOrEmpty(apiKey))
                {
                    client.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
                }

                for (int page = 0; page < 20; page++)
                {
                    string requestUrl = root + "/v1beta/models?pageSize=200";
                    if (!string.IsNullOrEmpty(pageToken))
                    {
                        requestUrl += "&pageToken=" + Uri.EscapeDataString(pageToken);
                    }

                    using (var response = await client.GetAsync(requestUrl))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"모델 목록 요청 실패 ({response.StatusCode}): {responseBody}");
                        }

                        using (var doc = JsonDocument.Parse(responseBody))
                        {
                            if (doc.RootElement.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in modelsArray.EnumerateArray())
                                {
                                    if (!item.TryGetProperty("name", out var nameElement))
                                    {
                                        continue;
                                    }

                                    string? name = nameElement.GetString();
                                    if (string.IsNullOrWhiteSpace(name))
                                    {
                                        continue;
                                    }

                                    if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                                    {
                                        name = name.Substring("models/".Length);
                                    }

                                    if (models.Contains(name, StringComparer.Ordinal))
                                    {
                                        continue;
                                    }

                                    if (item.TryGetProperty("supportedGenerationMethods", out var methods) &&
                                        methods.ValueKind == JsonValueKind.Array)
                                    {
                                        bool supportsGenerateContent = false;
                                        foreach (var method in methods.EnumerateArray())
                                        {
                                            if (string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase))
                                            {
                                                supportsGenerateContent = true;
                                                break;
                                            }
                                        }

                                        if (!supportsGenerateContent)
                                        {
                                            continue;
                                        }
                                    }

                                    models.Add(name);
                                }
                            }

                            if (doc.RootElement.TryGetProperty("nextPageToken", out var nextToken) &&
                                nextToken.ValueKind == JsonValueKind.String)
                            {
                                pageToken = nextToken.GetString();
                                if (string.IsNullOrEmpty(pageToken))
                                {
                                    break;
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
            }

            return models;
        }
    }
}
