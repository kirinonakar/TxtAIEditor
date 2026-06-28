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
                { ("opencode-go", "glm-5.2"), (1000000, 131072) },
                { ("opencode-go", "kimi-k2.7-code"), (262144, 262144) },
                { ("opencode-go", "minimax-m3"), (512000, 131072) },
                { ("opencode-go", "mimo-v2.5"), (1000000, 128000) },
                { ("opencode-go", "mimo-v2.5-pro"), (1048576, 128000) },
                { ("opencode-go", "mimo-v2-omni"), (262144, 128000) },
                { ("opencode-go", "qwen3.5-plus"), (262144, 65536) },
                { ("opencode-go", "qwen3.6-plus"), (1000000, 65536) },
                { ("opencode-go", "qwen3.7-max"), (1000000, 65536) },
                { ("opencode-go", "qwen3.7-plus"), (1000000, 65536) },

                { ("opencode", "gpt-5.4"), (1050000, 128000) },
                { ("opencode", "gpt-5.4-mini"), (400000, 128000) },
                { ("opencode", "gpt-5.4-nano"), (400000, 128000) },
                { ("opencode", "gpt-5.4-pro"), (1050000, 128000) },
                { ("opencode", "gpt-5.5"), (1050000, 128000) },
                { ("opencode", "gpt-5.5-pro"), (1050000, 128000) },
                { ("opencode", "claude-opus-4-7"), (1000000, 128000) },
                { ("opencode", "claude-sonnet-4-6"), (1000000, 64000) },
                { ("opencode", "gemini-3-flash"), (1048576, 65536) },
                { ("opencode", "gemini-3.1-pro"), (1048576, 65536) },
                { ("opencode", "gemini-3.5-flash"), (1048576, 65536) },
                { ("opencode", "grok-build-0.1"), (256000, 256000) },
                { ("opencode", "kimi-k2.6"), (262144, 65536) },
                { ("opencode", "minimax-m2.5"), (204800, 131072) },
                { ("opencode", "minimax-m2.7"), (204800, 131072) },
                { ("opencode", "minimax-m3"), (512000, 131072) },
                { ("opencode", "qwen3.5-plus"), (262144, 65536) },
                { ("opencode", "qwen3.6-plus"), (262144, 65536) },
                { ("opencode", "qwen3.7-max"), (1000000, 65536) },
                { ("opencode", "qwen3.7-plus"), (1000000, 65536) },
                { ("opencode", "claude-fable-5"), (1000000, 128000) },
                { ("opencode", "claude-opus-4-8"), (1000000, 128000) },
                { ("opencode", "claude-opus-4-6"), (1000000, 128000) },
                { ("opencode", "deepseek-v4-pro"), (1000000, 384000) },
                { ("opencode", "deepseek-v4-flash"), (1000000, 384000) },
                { ("opencode", "deepseek-v4-flash-free"), (200000, 128000) },
                { ("opencode", "glm-5.2"), (1000000, 131072) },
                { ("opencode", "kimi-k2.5"), (262144, 65536) },
                { ("opencode", "big-pickle"), (200000, 32000) },
                { ("opencode", "mimo-v2.5-free"), (200000, 32000) },
                { ("opencode", "north-mini-code-free"), (256000, 64000) },

                { ("openai", "gpt-5.4"), (1050000, 128000) },
                { ("openai", "gpt-5.4-mini"), (400000, 128000) },
                { ("openai", "gpt-5.4-nano"), (400000, 128000) },
                { ("openai", "gpt-5.4-pro"), (1050000, 128000) },
                { ("openai", "gpt-5.5"), (1050000, 128000) },
                { ("openai", "gpt-5.5-pro"), (1050000, 128000) },

                { ("google", "gemini-flash-lite-latest"), (1048576, 65536) },
                { ("google", "gemini-flash-latest"), (1048576, 65536) },
                { ("google", "gemini-pro-latest"), (1048576, 65536) },
                { ("google", "gemini-3.5-flash"), (1048576, 65536) },
                { ("google", "gemini-3.1-pro-preview"), (1048576, 65536) },
                { ("google", "gemini-3-pro-image-preview"), (1048576, 65536) },
                { ("google", "gemma-4-26b-a4b-it"), (262144, 32768) },
                { ("google", "gemma-4-31b-it"), (262144, 32768) },

                { ("ollama-cloud", "qwen3.5:397b"), (262144, 65536) },
                { ("ollama-cloud", "glm-5.2"), (976000, 131072) },
                { ("ollama-cloud", "nemotron-3-nano:30b"), (1048576, 131072) },
                { ("ollama-cloud", "ministral-3:14b"), (262144, 128000) },
                { ("ollama-cloud", "nemotron-3-super"), (262144, 65536) },
                { ("ollama-cloud", "deepseek-v3.1:671b"), (163840, 163840) },
                { ("ollama-cloud", "devstral-2:123b"), (262144, 262144) },
                { ("ollama-cloud", "devstral-small-2:24b"), (262144, 262144) },
                { ("ollama-cloud", "gemini-3-flash-preview"), (1048576, 65536) },
                { ("ollama-cloud", "minimax-m3"), (512000, 131072) },
                { ("ollama-cloud", "deepseek-v4-flash"), (1048576, 1048576) },
                { ("ollama-cloud", "nemotron-3-ultra"), (262144, 128000) },
                { ("ollama-cloud", "qwen3-coder:480b"), (262144, 65536) },
                { ("ollama-cloud", "deepseek-v4-pro"), (1048576, 1048576) },
                { ("ollama-cloud", "ministral-3:8b"), (262144, 128000) },
                { ("ollama-cloud", "kimi-k2.7-code"), (262144, 262144) },
                { ("ollama-cloud", "gemma4:31b"), (262144, 262144) },
                { ("ollama-cloud", "qwen3-coder-next"), (262144, 65536) },
                { ("ollama-cloud", "mistral-large-3:675b"), (262144, 262144) },

                { ("openrouter", "openai/gpt-5.4"), (1050000, 128000) },
                { ("openrouter", "openai/gpt-5.4-mini"), (400000, 128000) },
                { ("openrouter", "openai/gpt-5.4-nano"), (400000, 128000) },
                { ("openrouter", "openai/gpt-5.4-pro"), (1050000, 128000) },
                { ("openrouter", "openai/gpt-5.5"), (1050000, 128000) },
                { ("openrouter", "openai/gpt-5.5-pro"), (1050000, 128000) },
                { ("openrouter", "~anthropic/claude-fable-latest"), (1000000, 128000) },
                { ("openrouter", "anthropic/claude-opus-4.8"), (1000000, 128000) },
                { ("openrouter", "anthropic/claude-opus-4.7"), (1000000, 128000) },
                { ("openrouter", "anthropic/claude-opus-4.6"), (1000000, 128000) },
                { ("openrouter", "anthropic/claude-sonnet-4.6"), (1000000, 128000) },
                { ("openrouter", "deepseek/deepseek-chat"), (128000, 16000) },
                { ("openrouter", "deepseek/deepseek-chat-v3.1"), (163840, 32768) },
                { ("openrouter", "deepseek/deepseek-v4-flash"), (1000000, 65536) },
                { ("openrouter", "deepseek/deepseek-v4-pro"), (1048576, 384000) },
                { ("openrouter", "google/gemini-3-flash-preview"), (1048576, 65535) },
                { ("openrouter", "google/gemini-3.1-pro-preview"), (1048576, 65536) },
                { ("openrouter", "google/gemini-3.5-flash"), (1048576, 65536) },
                { ("openrouter", "google/gemma-4-26b-a4b-it"), (262144, 262144) },
                { ("openrouter", "google/gemma-4-31b-it"), (262144, 262144) },
                { ("openrouter", "google/gemma-4-31b-it:free"), (262144, 8192) },
                { ("openrouter", "moonshotai/kimi-k2.5"), (256000, 256000) },
                { ("openrouter", "moonshotai/kimi-k2.6"), (262144, 262144) },
                { ("openrouter", "moonshotai/kimi-k2.7-code"), (262144, 16384) },
                { ("openrouter", "minimax/minimax-m3"), (524288, 512000) },
                { ("openrouter", "mistralai/devstral-2512"), (262144, 262144) },
                { ("openrouter", "mistralai/ministral-14b-2512"), (262144, 262144) },
                { ("openrouter", "mistralai/ministral-8b-2512"), (262144, 262144) },
                { ("openrouter", "mistralai/mistral-large-2512"), (262144, 262144) },
                { ("openrouter", "nvidia/nemotron-3-nano-30b-a3b"), (262144, 228000) },
                { ("openrouter", "nvidia/nemotron-3-super-120b-a12b"), (262144, 16384) },
                { ("openrouter", "nvidia/nemotron-3-super-120b-a12b:free"), (262144, 262144) },
                { ("openrouter", "nvidia/nemotron-3-ultra-550b-a55b"), (262144, 16384) },
                { ("openrouter", "nvidia/nemotron-3-ultra-550b-a55b:free"), (1000000, 65536) },
                { ("openrouter", "qwen/qwen3-coder"), (262144, 65536) },
                { ("openrouter", "qwen/qwen3-coder-next"), (262144, 262144) },
                { ("openrouter", "qwen/qwen3.5-397b-a17b"), (131072, 64000) },
                { ("openrouter", "qwen/qwen3.5-plus-20260420"), (1000000, 65536) },
                { ("openrouter", "qwen/qwen3.6-plus"), (1000000, 65536) },
                { ("openrouter", "qwen/qwen3.7-max"), (1000000, 65536) },
                { ("openrouter", "qwen/qwen3.7-plus"), (1000000, 65536) },
                { ("openrouter", "x-ai/grok-build-0.1"), (256000, 256000) },
                { ("openrouter", "xiaomi/mimo-v2.5"), (32000, 131072) },
                { ("openrouter", "xiaomi/mimo-v2.5-pro"), (1048576, 131072) },
                { ("openrouter", "z-ai/glm-5.2"), (1048576, 32768) },
                { ("openrouter", "cohere/north-mini-code:free"), (256000, 64000) },
            };

        public static string MapProviderKey(string appProvider)
        {
            string p = (appProvider ?? string.Empty).ToLowerInvariant().Trim();
            return p switch
            {
                "opencode go" or "opencodego" or "go" => "opencode-go",
                "opencode zen" or "opencodezen" or "zen" => "opencode",
                "gemini" => "google",
                "openai" or "openai oauth" or "openaioauth" => "openai",
                "ollama" => "ollama",
                "ollama cloud" or "ollamacloud" => "ollama-cloud",
                "openrouter" => "openrouter",
                "lm studio" or "lmstudio" => "lmstudio",
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

        public static async Task<(int context, int output)> GetBestLimitsAsync(string appProvider, string model, CancellationToken cancellationToken = default)
        {
            return await GetLimitsAsync(appProvider, model, cancellationToken);
        }

        public static (int context, int output) GetBestCachedLimits(string appProvider, string model)
        {
            return GetCachedLimits(appProvider, model);
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

            if (providerKey == "google" && modelKey == "gemini-pro-latest")
            {
                if (_fallback.TryGetValue((providerKey, "gemini-3.1-pro-preview"), out var proFb))
                    return proFb;
                return (1048576, 65536);
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
