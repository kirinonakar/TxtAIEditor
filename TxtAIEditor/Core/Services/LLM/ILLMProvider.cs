using System;
using System.Collections.Generic;
using System.Text;
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

    public interface ILLMProvider
    {
        Task<string> GenerateCompletionAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, IReadOnlyList<LlmTool>? tools = null);

        Task GenerateCompletionStreamAsync(string endpoint, string apiKey, string model, string systemPrompt, string userContent, Func<string, Task> onChunk, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null);
    }
}
