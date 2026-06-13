using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsLlmModelFetcher
    {
        public static async Task<IReadOnlyList<string>> FetchModelsAsync(string endpoint)
        {
            string baseEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:1234/v1" : endpoint.Trim();
            string requestUrl = baseEndpoint.TrimEnd('/') + "/models";

            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
            using (var response = await client.GetAsync(requestUrl))
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"모델 목록 요청 실패 ({response.StatusCode}): {responseBody}");
                }

                using (var doc = JsonDocument.Parse(responseBody))
                {
                    var models = new List<string>();
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

                    return models;
                }
            }
        }
    }
}
