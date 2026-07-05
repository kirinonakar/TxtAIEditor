using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    public class ResponseTruncatedException : Exception
    {
        public ResponseTruncatedException() : base("Response truncated due to token limit.") { }
        public ResponseTruncatedException(string message) : base(message) { }
    }

    public class LlmTool
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public object Parameters { get; set; } = new { type = "object", properties = new { } };
    }

    public sealed class LlmTokenUsage
    {
        public string Provider { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public DateTimeOffset? ObservedAt { get; init; }
        public int? PromptTokens { get; init; }
        public int? CompletionTokens { get; init; }
        public int? TotalTokens { get; init; }
        public int? CachedTokens { get; init; }

        public bool HasAny =>
            PromptTokens.HasValue ||
            CompletionTokens.HasValue ||
            TotalTokens.HasValue ||
            CachedTokens.HasValue;

        public static LlmTokenUsage? FromJson(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new LlmTokenUsage
            {
                PromptTokens = GetInt32(usage, "prompt_tokens") ?? GetInt32(usage, "input_tokens"),
                CompletionTokens = GetInt32(usage, "completion_tokens") ?? GetInt32(usage, "output_tokens"),
                TotalTokens = GetInt32(usage, "total_tokens"),
                CachedTokens =
                    GetNestedInt32(usage, "prompt_tokens_details", "cached_tokens") ??
                    GetNestedInt32(usage, "input_tokens_details", "cached_tokens") ??
                    GetInt32(usage, "cached_tokens") ??
                    GetInt32(usage, "prompt_cache_hit_tokens") ??
                    GetInt32(usage, "cache_read_input_tokens")
            };

            return result.HasAny ? result : null;
        }

        public LlmTokenUsage WithContext(string provider, string model, DateTimeOffset observedAt)
        {
            return new LlmTokenUsage
            {
                Provider = provider ?? string.Empty,
                Model = model ?? string.Empty,
                ObservedAt = observedAt,
                PromptTokens = PromptTokens,
                CompletionTokens = CompletionTokens,
                TotalTokens = TotalTokens,
                CachedTokens = CachedTokens
            };
        }

        private static int? GetNestedInt32(JsonElement root, string objectName, string propertyName)
        {
            if (!root.TryGetProperty(objectName, out var container) ||
                container.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return GetInt32(container, propertyName);
        }

        private static int? GetInt32(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt32(out int value))
            {
                return value;
            }

            if (property.ValueKind == JsonValueKind.String &&
                int.TryParse(property.GetString(), out value))
            {
                return value;
            }

            return null;
        }
    }

    public sealed class LlmTokenUsageBucket
    {
        public string Provider { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public int RequestCount { get; init; }
        public long PromptTokens { get; init; }
        public long CompletionTokens { get; init; }
        public long TotalTokens { get; init; }
        public long CachedTokens { get; init; }
    }

    public sealed class LlmTokenUsagePeriodBucket
    {
        public string Period { get; init; } = string.Empty;
        public int RequestCount { get; init; }
        public long PromptTokens { get; init; }
        public long CompletionTokens { get; init; }
        public long TotalTokens { get; init; }
        public long CachedTokens { get; init; }
    }

    public sealed class LlmTokenUsageStats
    {
        public int RequestCount { get; init; }
        public long PromptTokens { get; init; }
        public long CompletionTokens { get; init; }
        public long TotalTokens { get; init; }
        public long CachedTokens { get; init; }
        public LlmTokenUsage? LastUsage { get; init; }
        public IReadOnlyList<LlmTokenUsageBucket> ByProviderModel { get; init; } = Array.Empty<LlmTokenUsageBucket>();
        public IReadOnlyList<LlmTokenUsagePeriodBucket> ByDay { get; init; } = Array.Empty<LlmTokenUsagePeriodBucket>();
        public IReadOnlyList<LlmTokenUsagePeriodBucket> ByMonth { get; init; } = Array.Empty<LlmTokenUsagePeriodBucket>();

        public bool HasAny =>
            RequestCount > 0 ||
            PromptTokens > 0 ||
            CompletionTokens > 0 ||
            TotalTokens > 0 ||
            CachedTokens > 0;
    }

    internal static class LlmUsageReporter
    {
        public static async Task TryReportUsageAsync(JsonElement root, Func<LlmTokenUsage, Task>? onUsage)
        {
            if (onUsage == null ||
                root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!root.TryGetProperty("usage", out var usageElement) &&
                root.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.Object)
            {
                messageElement.TryGetProperty("usage", out usageElement);
            }

            var usage = LlmTokenUsage.FromJson(usageElement);
            if (usage != null)
            {
                await onUsage(usage);
            }
        }
    }

    public class StreamToolCallAccumulator
    {
        public string Name { get; set; } = string.Empty;
        public bool SentStartTag { get; set; }
        public bool SentArgumentsHeader { get; set; }
        public StringBuilder Arguments { get; } = new StringBuilder();
    }

    internal static class LlmToolCallTextFormatter
    {
        public static string FormatFunctionToolCall(string functionName, string argumentsJson)
        {
            string nameJson = JsonSerializer.Serialize(functionName ?? string.Empty);
            string arguments = NormalizeArgumentsJson(argumentsJson);
            return $"<tool_call>{{\"name\":{nameJson},\"arguments\":{arguments}}}</tool_call>";
        }

        public static string FormatAssistantResponseWithFunctionToolCall(string? content, JsonElement toolCall)
        {
            if (!toolCall.TryGetProperty("function", out var function))
            {
                return content ?? string.Empty;
            }

            string functionName = function.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString() ?? string.Empty
                : string.Empty;
            string argumentsJson = function.TryGetProperty("arguments", out var argumentsProperty)
                ? argumentsProperty.GetString() ?? string.Empty
                : string.Empty;
            string toolCallText = FormatFunctionToolCall(functionName, argumentsJson);

            if (string.IsNullOrWhiteSpace(content))
            {
                return toolCallText;
            }

            return content.TrimEnd() + Environment.NewLine + Environment.NewLine + toolCallText;
        }

        private static string NormalizeArgumentsJson(string argumentsJson)
        {
            string arguments = (argumentsJson ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "{}";
            }

            try
            {
                using var document = JsonDocument.Parse(arguments);
                return document.RootElement.GetRawText();
            }
            catch
            {
                return "{}";
            }
        }
    }

    public interface ILLMProvider
    {
        Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null);

        Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null);
    }
}
