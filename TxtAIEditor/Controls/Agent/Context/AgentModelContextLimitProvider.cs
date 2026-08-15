using System;
using System.Text.Json;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentModelContextLimitProvider
    {
        private readonly ICredentialService? _credentialService;
        private int? _lmStudioContextLimitCache;
        private string? _lmStudioLastFetchedModel;
        private string? _lmStudioLastFetchedEndpoint;
        private bool _lmStudioFetchInProgress;
        private DateTime _lmStudioLastFetchedTime = DateTime.MinValue;
        private int? _unslothContextLimitCache;
        private string? _unslothLastFetchedModel;
        private string? _unslothLastFetchedEndpoint;
        private bool _unslothFetchInProgress;
        private DateTime _unslothLastFetchedTime = DateTime.MinValue;
        private bool _modelsDevPriming;

        public AgentModelContextLimitProvider(ICredentialService? credentialService = null)
        {
            _credentialService = credentialService;
        }

        public void ResetContextLimitCache()
        {
            _lmStudioContextLimitCache = null;
            _lmStudioLastFetchedModel = null;
            _lmStudioLastFetchedEndpoint = null;
            _lmStudioLastFetchedTime = DateTime.MinValue;
            _unslothContextLimitCache = null;
            _unslothLastFetchedModel = null;
            _unslothLastFetchedEndpoint = null;
            _unslothLastFetchedTime = DateTime.MinValue;
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

            if (provider.Contains("unsloth", StringComparison.Ordinal))
            {
                bool sameRequest = string.Equals(
                    settings.LlmModel,
                    _unslothLastFetchedModel,
                    StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        settings.LlmEndpoint,
                        _unslothLastFetchedEndpoint,
                        StringComparison.OrdinalIgnoreCase);
                bool needFetch = !sameRequest ||
                                 !_unslothContextLimitCache.HasValue ||
                                 (DateTime.Now - _unslothLastFetchedTime) > TimeSpan.FromSeconds(10);

                if (needFetch && !_unslothFetchInProgress)
                {
                    _ = Task.Run(() => FetchUnslothContextLimitAsync(
                        settings.LlmEndpoint ?? string.Empty,
                        settings.LlmModel ?? string.Empty,
                        settings.LlmProvider ?? string.Empty,
                        onContextLimitChanged));
                }

                if (sameRequest && _unslothContextLimitCache.HasValue)
                {
                    return _unslothContextLimitCache.Value;
                }
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

            if (provider.Contains("upstage"))
            {
                if (model.Contains("solar-pro4")) return 1000000;
                if (model.Contains("solar-pro3")) return 131072;
                if (model.Contains("solar-pro2")) return 65536;
                if (model.Contains("solar-mini")) return 32768;
                return 131072;
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

        private async Task FetchUnslothContextLimitAsync(
            string endpoint,
            string modelName,
            string providerName,
            Action onContextLimitChanged)
        {
            if (_unslothFetchInProgress) return;
            _unslothFetchInProgress = true;

            try
            {
                string apiEndpoint = string.IsNullOrWhiteSpace(endpoint)
                    ? "http://localhost:8888/v1"
                    : endpoint.Trim();
                string baseUrl = GetEndpointOrigin(apiEndpoint);
                string[] requestUrls =
                {
                    apiEndpoint.TrimEnd('/') + "/models",
                    baseUrl.TrimEnd('/') + "/api/monitor",
                    baseUrl.TrimEnd('/') + "/api/models/list"
                };

                using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    string apiKey = GetApiKey(providerName);
                    foreach (string requestUrl in requestUrls)
                    {
                        using var request = new System.Net.Http.HttpRequestMessage(
                            System.Net.Http.HttpMethod.Get,
                            requestUrl);
                        if (!string.IsNullOrWhiteSpace(apiKey))
                        {
                            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                                "Bearer",
                                apiKey);
                        }

                        using var response = await client.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                        {
                            continue;
                        }

                        string body = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(body);
                        if (!TryFindUnslothContextLimit(doc.RootElement, modelName, out int contextLimit))
                        {
                            continue;
                        }

                        _unslothContextLimitCache = contextLimit;
                        _unslothLastFetchedModel = modelName;
                        _unslothLastFetchedEndpoint = endpoint;
                        _unslothLastFetchedTime = DateTime.Now;
                        onContextLimitChanged();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to fetch Unsloth context length: {ex.Message}");
            }
            finally
            {
                _unslothFetchInProgress = false;
            }
        }

        private string GetApiKey(string providerName)
        {
            if (_credentialService == null || string.IsNullOrWhiteSpace(providerName))
            {
                return string.Empty;
            }

            try
            {
                return _credentialService.ReadCredential($"TxtAIEditor_LLM_{providerName}") ?? string.Empty;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read {providerName} API key: {ex.Message}");
                return string.Empty;
            }
        }

        private static string GetEndpointOrigin(string endpoint)
        {
            try
            {
                var uri = new Uri(endpoint);
                return $"{uri.Scheme}://{uri.Authority}";
            }
            catch
            {
                return "http://localhost:8888";
            }
        }

        private static bool TryFindUnslothContextLimit(
            JsonElement root,
            string modelName,
            out int contextLimit)
        {
            contextLimit = 0;

            if (root.ValueKind == JsonValueKind.Array)
            {
                return TryFindUnslothModelArrayContext(root, modelName, out contextLimit);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (TryGetUnslothContextLength(root, out contextLimit))
            {
                return true;
            }

            foreach (string propertyName in new[] { "data", "models", "items", "model_list" })
            {
                if (root.TryGetProperty(propertyName, out var array) &&
                    array.ValueKind == JsonValueKind.Array &&
                    TryFindUnslothModelArrayContext(array, modelName, out contextLimit))
                {
                    return true;
                }
            }

            foreach (string propertyName in new[] { "model", "active_model", "loaded_model", "model_info" })
            {
                if (root.TryGetProperty(propertyName, out var model) &&
                    model.ValueKind == JsonValueKind.Object &&
                    TryGetUnslothContextLength(model, out contextLimit))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindUnslothModelArrayContext(
            JsonElement array,
            string modelName,
            out int contextLimit)
        {
            contextLimit = 0;
            JsonElement? exactMatch = null;
            JsonElement? partialMatch = null;
            JsonElement? loadedModel = null;
            JsonElement? firstModel = null;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                firstModel ??= item;
                if (IsUnslothModelMatch(item, modelName, allowPartial: false))
                {
                    exactMatch ??= item;
                }
                else if (IsUnslothModelMatch(item, modelName, allowPartial: true))
                {
                    partialMatch ??= item;
                }

                if (IsUnslothLoadedModel(item))
                {
                    loadedModel ??= item;
                }
            }

            foreach (JsonElement? candidate in new[] { exactMatch, partialMatch, loadedModel, firstModel })
            {
                if (candidate.HasValue &&
                    TryGetUnslothContextLength(candidate.Value, out contextLimit))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnslothModelMatch(JsonElement item, string modelName, bool allowPartial)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return false;
            }

            foreach (string propertyName in new[]
            {
                "id", "name", "model", "model_name", "model_id", "path", "model_path", "repo_id", "display_name"
            })
            {
                string? value = GetJsonString(item, propertyName);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (value.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
                    (allowPartial &&
                     (value.Contains(modelName, StringComparison.OrdinalIgnoreCase) ||
                      modelName.Contains(value, StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsUnslothLoadedModel(JsonElement item)
        {
            foreach (string propertyName in new[] { "loaded", "is_loaded", "active", "is_active" })
            {
                if (item.TryGetProperty(propertyName, out var value) &&
                    value.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }

            foreach (string propertyName in new[] { "status", "state" })
            {
                string? value = GetJsonString(item, propertyName);
                if (value != null &&
                    (value.Equals("loaded", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("active", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("ready", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetUnslothContextLength(JsonElement item, out int contextLimit)
        {
            contextLimit = 0;

            foreach (string propertyName in new[]
            {
                "context_length", "context_window", "requested_context_length", "max_seq_length"
            })
            {
                if (TryGetPositiveJsonInt(item, propertyName, out contextLimit))
                {
                    return true;
                }
            }

            foreach (string propertyName in new[]
            {
                "config", "settings", "runtime", "model_config", "model_info", "backend", "loaded_model", "llama_backend"
            })
            {
                if (item.TryGetProperty(propertyName, out var nested) &&
                    nested.ValueKind == JsonValueKind.Object &&
                    TryGetUnslothContextLength(nested, out contextLimit))
                {
                    return true;
                }
            }

            foreach (string propertyName in new[] { "max_context_length", "native_context_length" })
            {
                if (TryGetPositiveJsonInt(item, propertyName, out contextLimit))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? GetJsonString(JsonElement parent, string propName)
        {
            return parent.TryGetProperty(propName, out var prop) &&
                   prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }

        private static bool TryGetPositiveJsonInt(JsonElement parent, string propName, out int value)
        {
            return TryGetJsonInt(parent, propName, out value) && value > 0;
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
