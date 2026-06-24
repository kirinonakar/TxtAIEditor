using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    public static class ModelsDevCatalog
    {
        private const string ApiUrl = "https://models.dev/api.json";
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static JsonDocument? _doc;
        private static DateTime _loadedAt = DateTime.MinValue;
        private static bool _loadFailed;
        private static bool _priming;
        private static readonly TimeSpan _refreshInterval = TimeSpan.FromHours(24);

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private static readonly Dictionary<(string providerKey, string model), (int context, int output)> _fallback =
            new()
            {
                { ("opencode-go", "deepseek-v4-flash"), (1000000, 384000) },
                { ("opencode-go", "deepseek-v4-pro"), (1000000, 384000) },
                { ("opencode-go", "glm-5"), (202752, 32768) },
                { ("opencode-go", "glm-5.1"), (202752, 32768) },
                { ("opencode-go", "glm-5.2"), (1000000, 131072) },
                { ("opencode-go", "kimi-k2.5"), (262144, 65536) },
                { ("opencode-go", "kimi-k2.6"), (262144, 65536) },
                { ("opencode-go", "kimi-k2.7-code"), (262144, 262144) },
                { ("opencode-go", "minimax-m2.5"), (204800, 65536) },
                { ("opencode-go", "minimax-m2.7"), (204800, 131072) },
                { ("opencode-go", "minimax-m3"), (512000, 131072) },
                { ("opencode-go", "mimo-v2.5"), (1000000, 128000) },
                { ("opencode-go", "mimo-v2.5-pro"), (1048576, 128000) },
                { ("opencode-go", "mimo-v2-omni"), (262144, 128000) },
                { ("opencode-go", "mimo-v2-pro"), (1048576, 128000) },
                { ("opencode-go", "qwen3.5-plus"), (262144, 65536) },
                { ("opencode-go", "qwen3.6-plus"), (1000000, 65536) },
                { ("opencode-go", "qwen3.7-max"), (1000000, 65536) },
                { ("opencode-go", "qwen3.7-plus"), (1000000, 65536) },

                { ("opencode", "deepseek-v4-flash"), (1000000, 384000) },
                { ("opencode", "deepseek-v4-pro"), (1000000, 384000) },
                { ("opencode", "glm-5"), (204800, 131072) },
                { ("opencode", "glm-5.1"), (204800, 131072) },
                { ("opencode", "glm-5.2"), (1000000, 131072) },
                { ("opencode", "gpt-5"), (400000, 128000) },
                { ("opencode", "gpt-5-codex"), (400000, 128000) },
                { ("opencode", "gpt-5-nano"), (400000, 128000) },
                { ("opencode", "gpt-5.4"), (1050000, 128000) },
                { ("opencode", "gpt-5.4-mini"), (400000, 128000) },
                { ("opencode", "gpt-5.4-nano"), (400000, 128000) },
                { ("opencode", "gpt-5.4-pro"), (1050000, 128000) },
                { ("opencode", "gpt-5.5"), (1050000, 128000) },
                { ("opencode", "gpt-5.5-pro"), (1050000, 128000) },
                { ("opencode", "claude-haiku-4-5"), (200000, 64000) },
                { ("opencode", "claude-opus-4-7"), (1000000, 128000) },
                { ("opencode", "claude-sonnet-4-6"), (1000000, 64000) },
                { ("opencode", "gemini-3-flash"), (1048576, 65536) },
                { ("opencode", "gemini-3.1-pro"), (1048576, 65536) },
                { ("opencode", "gemini-3.5-flash"), (1048576, 65536) },
                { ("opencode", "grok-build-0.1"), (256000, 256000) },
                { ("opencode", "kimi-k2.6"), (262144, 65536) },
                { ("opencode", "minimax-m2.5"), (204800, 131072) },
                { ("opencode", "minimax-m2.7"), (204800, 131072) },
                { ("opencode", "qwen3.5-plus"), (262144, 65536) },
                { ("opencode", "qwen3.6-plus"), (262144, 65536) },
            };

        public static string MapProviderKey(string appProvider)
        {
            string p = (appProvider ?? string.Empty).ToLowerInvariant().Trim();
            return p switch
            {
                "opencode go" or "opencodego" or "go" => "opencode-go",
                "opencode zen" or "opencodezen" or "zen" => "opencode",
                _ => p.Replace(" ", "-"),
            };
        }

        public static async Task PrimeAsync(CancellationToken cancellationToken = default)
        {
            if (_doc != null && (DateTime.Now - _loadedAt) < _refreshInterval) return;
            if (_priming) return;
            _priming = true;
            try
            {
                await EnsureLoadedAsync(cancellationToken);
            }
            catch
            {
            }
            finally
            {
                _priming = false;
            }
        }

        public static async Task<(int context, int output)> GetLimitsAsync(string appProvider, string model, CancellationToken cancellationToken = default)
        {
            await EnsureLoadedAsync(cancellationToken);
            return Resolve(appProvider, model);
        }

        public static (int context, int output) GetCachedLimits(string appProvider, string model)
        {
            return Resolve(appProvider, model);
        }

        public static bool IsLoaded => _doc != null;

        private static (int context, int output) Resolve(string appProvider, string model)
        {
            if (string.IsNullOrEmpty(model)) return (0, 0);
            string providerKey = MapProviderKey(appProvider);
            string modelKey = model.ToLowerInvariant();

            if (_doc != null)
            {
                try
                {
                    if (_doc.RootElement.TryGetProperty(providerKey, out var prov) &&
                        prov.TryGetProperty("models", out var models) &&
                        models.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var m in models.EnumerateObject())
                        {
                            if (!string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)) continue;
                            return ExtractLimit(m.Value);
                        }
                    }
                }
                catch { }
            }

            if (_fallback.TryGetValue((providerKey, modelKey), out var fb))
            {
                return fb;
            }

            return (0, 0);
        }

        private static (int context, int output) ExtractLimit(JsonElement modelEl)
        {
            int context = 0;
            int output = 0;
            if (modelEl.TryGetProperty("limit", out var limit))
            {
                if (limit.TryGetProperty("context", out var ctx) && ctx.TryGetInt32(out int c)) context = c;
                if (limit.TryGetProperty("output", out var outEl) && outEl.TryGetInt32(out int o)) output = o;
            }
            return (context, output);
        }

        private static async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_doc != null && (DateTime.Now - _loadedAt) < _refreshInterval && !_loadFailed) return;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_doc != null && (DateTime.Now - _loadedAt) < _refreshInterval && !_loadFailed) return;

                string json = await _httpClient.GetStringAsync(ApiUrl, cancellationToken);
                var newDoc = JsonDocument.Parse(json);
                var oldDoc = _doc;
                _doc = newDoc;
                oldDoc?.Dispose();
                _loadedAt = DateTime.Now;
                _loadFailed = false;
            }
            catch
            {
                _loadFailed = true;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
