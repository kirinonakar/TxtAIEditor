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
        Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null);

        Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null);
    }
}
