using System;
using System.Collections.Generic;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsLlmModelCatalog
    {
        public static IReadOnlyList<string> ProviderNames { get; } = new[]
        {
            "Gemini",
            "OpenAI",
            "OpenAI OAuth",
            "OpenRouter",
            "LM Studio",
            "OpenCode Go",
            "OpenCode Zen",
            "Ollama",
            "Ollama Cloud"
        };

        public static int GetProviderIndex(string provider)
        {
            int providerIndex = Array.FindIndex(
                ProviderNames as string[] ?? new List<string>(ProviderNames).ToArray(),
                p => p.Equals(provider, StringComparison.OrdinalIgnoreCase));
            return providerIndex < 0 ? 1 : providerIndex;
        }

        public static bool SupportsRemoteModelFetch(string provider)
        {
            return provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("Ollama Cloud", StringComparison.OrdinalIgnoreCase);
        }

        public static bool SupportsThinkingLevel(string provider)
        {
            return provider.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("OpenAI OAuth", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("OpenAIOAuth", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsKnownDefaultEndpoint(string endpoint)
        {
            return string.IsNullOrWhiteSpace(endpoint) ||
                endpoint.Equals("https://api.openai.com/v1", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("http://127.0.0.1:10531/v1", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("https://openrouter.ai/api/v1", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("http://localhost:1234/v1", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("https://generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("https://opencode.ai/zen/go/v1", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("https://opencode.ai/zen/v1", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("http://localhost:11434/v1", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("https://ollama.com", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Equals("https://ollama.com/v1", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetDefaultEndpoint(string provider, string fallback)
        {
            return provider switch
            {
                "LM Studio" => "http://localhost:1234/v1",
                "OpenAI" => "https://api.openai.com/v1",
                "OpenAI OAuth" => "http://127.0.0.1:10531/v1",
                "OpenRouter" => "https://openrouter.ai/api/v1",
                "Gemini" => "https://generativelanguage.googleapis.com",
                "OpenCode Go" => "https://opencode.ai/zen/go/v1",
                "OpenCode Zen" => "https://opencode.ai/zen/v1",
                "Ollama" => "http://localhost:11434/v1",
                "Ollama Cloud" => "https://ollama.com",
                _ => fallback
            };
        }

        public static IReadOnlyList<string> GetStaticModels(string provider)
        {
            if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "gemini-flash-lite-latest",
                    "gemini-flash-latest",
                    "gemini-pro-latest",
                    "gemma-4-26b-a4b-it",
                    "gemma-4-31b-it"
                };
            }

            if (IsOpenAIProvider(provider))
            {
                return new[] { "gpt-5.5" };
            }

            if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "meta-llama/llama-3.3-70b-instruct:free",
                    "deepseek/deepseek-chat",
                    "google/gemini-2.5-flash",
                    "anthropic/claude-3.5-sonnet"
                };
            }

            if (provider.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "glm-5.2",
                    "glm-5.1",
                    "kimi-k2.7-code",
                    "kimi-k2.6",
                    "mimo-v2.5",
                    "mimo-v2.5-pro",
                    "minimax-m3",
                    "minimax-m2.7",
                    "qwen3.7-max",
                    "qwen3.7-plus",
                    "qwen3.6-plus",
                    "deepseek-v4-pro",
                    "deepseek-v4-flash"
                };
            }

            if (provider.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "gpt-5.5",
                    "gpt-5.5-pro",
                    "gpt-5.4",
                    "gpt-5.4-pro",
                    "gpt-5.4-mini",
                    "gpt-5.4-nano",
                    "gpt-5.3-codex",
                    "gpt-5.3-codex-spark",
                    "gpt-5.2",
                    "gpt-5.2-codex",
                    "gpt-5.1",
                    "gpt-5.1-codex",
                    "gpt-5.1-codex-max",
                    "gpt-5.1-codex-mini",
                    "gpt-5",
                    "gpt-5-codex",
                    "gpt-5-nano",
                    "claude-fable-5",
                    "claude-opus-4-8",
                    "claude-opus-4-7",
                    "claude-opus-4-6",
                    "claude-opus-4-5",
                    "claude-opus-4-1",
                    "claude-sonnet-4-6",
                    "claude-sonnet-4-5",
                    "claude-sonnet-4",
                    "claude-haiku-4-5",
                    "claude-3-5-haiku",
                    "gemini-3.5-flash",
                    "gemini-3.1-pro",
                    "gemini-3-flash",
                    "qwen3.7-max",
                    "qwen3.7-plus",
                    "qwen3.6-plus",
                    "qwen3.5-plus",
                    "deepseek-v4-pro",
                    "deepseek-v4-flash",
                    "minimax-m2.7",
                    "minimax-m2.5",
                    "glm-5.2",
                    "glm-5.1",
                    "glm-5",
                    "kimi-k2.5",
                    "kimi-k2.6",
                    "grok-build-0.1",
                    "big-pickle",
                    "mimo-v2.5-free",
                    "north-mini-code-free",
                    "nemotron-3-ultra-free",
                    "deepseek-v4-flash-free"
                };
            }

            if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "gemma4:31b",
                    "gemma4:26b",
                    "gemma4:12b",
                    "gemma4:4b"
                };
            }

            if (provider.Equals("Ollama Cloud", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "gpt-oss:20b",
                    "qwen3.5:397b",
                    "glm-5.2",
                    "gpt-oss:120b",
                    "nemotron-3-nano:30b",
                    "ministral-3:14b",
                    "gemma3:4b",
                    "nemotron-3-super",
                    "deepseek-v3.1:671b",
                    "devstral-2:123b",
                    "devstral-small-2:24b",
                    "gemini-3-flash-preview",
                    "minimax-m3",
                    "deepseek-v4-flash",
                    "nemotron-3-ultra",
                    "qwen3-coder:480b",
                    "deepseek-v4-pro",
                    "ministral-3:8b",
                    "kimi-k2.7-code",
                    "gemma4:31b",
                    "qwen3-coder-next",
                    "mistral-large-3:675b"
                };
            }

            return Array.Empty<string>();
        }

        public static string GetInitialModel(EditorSettings settings, string provider, string selectedModel)
        {
            if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return EnsureKnownModel(settings.LlmModelGemini, selectedModel, GetStaticModels(provider), "gemini-flash-lite-latest");
            }

            if (IsOpenAIProvider(provider))
            {
                return EnsureKnownModel(settings.LlmModelOpenAI, selectedModel, GetStaticModels(provider), "gpt-5.5");
            }

            if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOpenRouter) ? settings.LlmModelOpenRouter : selectedModel;
            }

            if (provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelLmStudio) ? settings.LlmModelLmStudio : selectedModel;
            }

            if (provider.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase))
            {
                string target = !string.IsNullOrEmpty(settings.LlmModelOpenCodeGo) ? settings.LlmModelOpenCodeGo : selectedModel;
                return string.IsNullOrEmpty(target) ? "deepseek-v4-flash" : target;
            }

            if (provider.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase))
            {
                string target = !string.IsNullOrEmpty(settings.LlmModelOpenCodeZen) ? settings.LlmModelOpenCodeZen : selectedModel;
                return string.IsNullOrEmpty(target) ? "gpt-5.5" : target;
            }

            if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOllama) ? settings.LlmModelOllama : selectedModel;
            }

            if (provider.Equals("Ollama Cloud", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOllamaCloud) ? settings.LlmModelOllamaCloud : selectedModel;
            }

            return selectedModel;
        }

        public static string GetModelForProviderChange(EditorSettings settings, string provider)
        {
            if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelGemini) ? settings.LlmModelGemini : "gemini-flash-lite-latest";
            }

            if (IsOpenAIProvider(provider))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOpenAI) ? settings.LlmModelOpenAI : "gpt-5.5";
            }

            if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOpenRouter) ? settings.LlmModelOpenRouter : "meta-llama/llama-3.3-70b-instruct:free";
            }

            if (provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelLmStudio) ? settings.LlmModelLmStudio : string.Empty;
            }

            if (provider.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOpenCodeGo) ? settings.LlmModelOpenCodeGo : "deepseek-v4-flash";
            }

            if (provider.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOpenCodeZen) ? settings.LlmModelOpenCodeZen : "gpt-5.5";
            }

            if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOllama) ? settings.LlmModelOllama : "llama3:latest";
            }

            if (provider.Equals("Ollama Cloud", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOllamaCloud) ? settings.LlmModelOllamaCloud : "llama3:latest";
            }

            return settings.LlmModel;
        }

        public static string GetRemoteFetchSelection(EditorSettings settings, string provider)
        {
            if (provider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelLmStudio) ? settings.LlmModelLmStudio : settings.LlmModel;
            }

            if (provider.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOpenCodeGo) ? settings.LlmModelOpenCodeGo : settings.LlmModel;
            }

            if (provider.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOpenCodeZen) ? settings.LlmModelOpenCodeZen : settings.LlmModel;
            }

            if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOllama) ? settings.LlmModelOllama : settings.LlmModel;
            }

            if (provider.Equals("Ollama Cloud", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(settings.LlmModelOllamaCloud) ? settings.LlmModelOllamaCloud : settings.LlmModel;
            }

            return !string.IsNullOrEmpty(settings.LlmModelOpenRouter) ? settings.LlmModelOpenRouter : settings.LlmModel;
        }

        public static void SaveProviderModel(EditorSettings settings)
        {
            if (settings.LlmProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelGemini = settings.LlmModel;
            }
            else if (IsOpenAIProvider(settings.LlmProvider))
            {
                settings.LlmModelOpenAI = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelOpenRouter = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelLmStudio = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("OpenCode Go", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelOpenCodeGo = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelOpenCodeZen = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelOllama = settings.LlmModel;
            }
            else if (settings.LlmProvider.Equals("Ollama Cloud", StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmModelOllamaCloud = settings.LlmModel;
            }
        }

        private static string EnsureKnownModel(string savedModel, string selectedModel, IReadOnlyList<string> knownModels, string fallback)
        {
            string target = !string.IsNullOrEmpty(savedModel) ? savedModel : selectedModel;
            if (string.IsNullOrEmpty(target))
            {
                return fallback;
            }

            foreach (string model in knownModels)
            {
                if (target.Equals(model, StringComparison.OrdinalIgnoreCase))
                {
                    return target;
                }
            }

            return fallback;
        }

        private static bool IsOpenAIProvider(string provider)
        {
            return provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("OpenAI OAuth", StringComparison.OrdinalIgnoreCase);
        }
    }
}
