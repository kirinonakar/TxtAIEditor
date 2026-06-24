using System;
using System.Text.Json;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentModelContextLimitProvider
    {
        private int? _lmStudioContextLimitCache;
        private string? _lmStudioLastFetchedModel;
        private string? _lmStudioLastFetchedEndpoint;
        private bool _lmStudioFetchInProgress;
        private DateTime _lmStudioLastFetchedTime = DateTime.MinValue;
        private bool _modelsDevPriming;

        public void ResetLmStudioCache()
        {
            _lmStudioContextLimitCache = null;
            _lmStudioLastFetchedModel = null;
            _lmStudioLastFetchedEndpoint = null;
            _lmStudioLastFetchedTime = DateTime.MinValue;
        }

        public int GetContextLimit(EditorSettings? settings, Action onContextLimitChanged)
        {
            if (settings == null)
            {
                return 0;
            }

            string model = (settings.LlmModel ?? string.Empty).ToLowerInvariant();
            string provider = (settings.LlmProvider ?? string.Empty).ToLowerInvariant();

            if (provider.Contains("lm studio") || provider.Contains("lmstudio"))
            {
                bool needFetch = !_lmStudioContextLimitCache.HasValue ||
                                 settings.LlmModel != _lmStudioLastFetchedModel ||
                                 settings.LlmEndpoint != _lmStudioLastFetchedEndpoint ||
                                 (DateTime.Now - _lmStudioLastFetchedTime) > TimeSpan.FromSeconds(10);

                if (needFetch && !_lmStudioFetchInProgress)
                {
                    _ = Task.Run(() => FetchLmStudioContextLimitAsync(
                        settings.LlmEndpoint ?? string.Empty,
                        settings.LlmModel ?? string.Empty,
                        onContextLimitChanged));
                }

                if (_lmStudioContextLimitCache.HasValue)
                {
                    return _lmStudioContextLimitCache.Value;
                }

                return 0;
            }

            if (!ModelsDevCatalog.IsLoaded && !_modelsDevPriming)
            {
                _modelsDevPriming = true;
                _ = Task.Run(async () =>
                {
                    try { await ModelsDevCatalog.PrimeAsync(); }
                    catch { }
                    finally
                    {
                        _modelsDevPriming = false;
                        onContextLimitChanged();
                    }
                });
            }

            var modelsDevLimits = ModelsDevCatalog.GetBestCachedLimits(
                settings.LlmProvider ?? string.Empty,
                settings.LlmModel ?? string.Empty);
            if (modelsDevLimits.context > 0)
            {
                return modelsDevLimits.context;
            }

            if (model.Contains("gemini"))
            {
                if (model.Contains("pro"))
                {
                    return 2000000;
                }
                if (model.Contains("flash"))
                {
                    return 1000000;
                }
                return 1000000;
            }

            if (provider.Contains("gemini"))
            {
                return 1000000;
            }

            return 256000;
        }

        private async Task FetchLmStudioContextLimitAsync(
            string endpoint,
            string modelName,
            Action onContextLimitChanged)
        {
            if (_lmStudioFetchInProgress) return;
            _lmStudioFetchInProgress = true;

            try
            {
                string baseUrl = "http://localhost:1234";
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    try
                    {
                        var uri = new Uri(endpoint);
                        baseUrl = $"{uri.Scheme}://{uri.Authority}";
                    }
                    catch { }
                }

                string requestUrl = baseUrl.TrimEnd('/') + "/api/v1/models";

                using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                using (var response = await client.GetAsync(requestUrl))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        string body = await response.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(body))
                        {
                            JsonElement arrayEl = default;
                            bool hasArray = false;

                            if (doc.RootElement.TryGetProperty("models", out var modelsProp) && modelsProp.ValueKind == JsonValueKind.Array)
                            {
                                arrayEl = modelsProp;
                                hasArray = true;
                            }
                            else if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                            {
                                arrayEl = dataProp;
                                hasArray = true;
                            }
                            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                arrayEl = doc.RootElement;
                                hasArray = true;
                            }

                            if (hasArray && TryFindLmStudioContextLimit(arrayEl, modelName, out int contextLimit))
                            {
                                _lmStudioContextLimitCache = contextLimit;
                                _lmStudioLastFetchedModel = modelName;
                                _lmStudioLastFetchedEndpoint = endpoint;
                                _lmStudioLastFetchedTime = DateTime.Now;
                                onContextLimitChanged();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to fetch LM Studio context length: {ex.Message}");
            }
            finally
            {
                _lmStudioFetchInProgress = false;
            }
        }

        private static bool TryFindLmStudioContextLimit(JsonElement arrayEl, string modelName, out int contextLimit)
        {
            contextLimit = 0;
            JsonElement? matchedItem = null;

            matchedItem ??= FindLmStudioModel(arrayEl, modelName, requireLoaded: true, allowPartial: false);
            matchedItem ??= FindLmStudioModel(arrayEl, modelName, requireLoaded: true, allowPartial: true);
            matchedItem ??= FindLmStudioModel(arrayEl, modelName, requireLoaded: false, allowPartial: false);
            matchedItem ??= FindLmStudioModel(arrayEl, modelName, requireLoaded: false, allowPartial: true);
            matchedItem ??= FindFirstLoadedLmStudioModel(arrayEl);
            if (matchedItem == null && arrayEl.GetArrayLength() > 0)
            {
                matchedItem = arrayEl[0];
            }

            if (!matchedItem.HasValue)
            {
                return false;
            }

            var item = matchedItem.Value;
            if (item.TryGetProperty("loaded_instances", out var loadedInstances) &&
                loadedInstances.ValueKind == JsonValueKind.Array &&
                loadedInstances.GetArrayLength() > 0)
            {
                var firstInstance = loadedInstances[0];
                if (firstInstance.TryGetProperty("config", out var config) &&
                    TryGetJsonInt(config, "context_length", out contextLimit))
                {
                    return true;
                }
            }

            return TryGetJsonInt(item, "max_context_length", out contextLimit);
        }

        private static JsonElement? FindLmStudioModel(
            JsonElement arrayEl,
            string modelName,
            bool requireLoaded,
            bool allowPartial)
        {
            foreach (var item in arrayEl.EnumerateArray())
            {
                string? id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                string? key = item.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : null;

                if (requireLoaded && !HasLoadedInstances(item))
                {
                    continue;
                }

                bool idMatches = id != null &&
                    (allowPartial
                        ? modelName.Contains(id, StringComparison.OrdinalIgnoreCase) || id.Contains(modelName, StringComparison.OrdinalIgnoreCase)
                        : id.Equals(modelName, StringComparison.OrdinalIgnoreCase));
                bool keyMatches = key != null &&
                    (allowPartial
                        ? modelName.Contains(key, StringComparison.OrdinalIgnoreCase) || key.Contains(modelName, StringComparison.OrdinalIgnoreCase)
                        : key.Equals(modelName, StringComparison.OrdinalIgnoreCase));

                if (idMatches || keyMatches)
                {
                    return item;
                }
            }

            return null;
        }

        private static JsonElement? FindFirstLoadedLmStudioModel(JsonElement arrayEl)
        {
            foreach (var item in arrayEl.EnumerateArray())
            {
                if (HasLoadedInstances(item))
                {
                    return item;
                }
            }

            return null;
        }

        private static bool HasLoadedInstances(JsonElement item)
        {
            return item.TryGetProperty("loaded_instances", out var loadedInstances) &&
                loadedInstances.ValueKind == JsonValueKind.Array &&
                loadedInstances.GetArrayLength() > 0;
        }

        private static bool TryGetJsonInt(JsonElement parent, string propName, out int value)
        {
            value = 0;
            if (parent.TryGetProperty(propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                {
                    return prop.TryGetInt32(out value);
                }
                if (prop.ValueKind == JsonValueKind.String)
                {
                    return int.TryParse(prop.GetString(), out value);
                }
            }

            return false;
        }
    }
}
