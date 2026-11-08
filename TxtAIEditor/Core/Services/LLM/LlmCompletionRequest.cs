using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    public sealed record LlmCompletionRequest
    {
        public string Endpoint { get; init; } = string.Empty;
        public string ApiKey { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string SystemPrompt { get; init; } = string.Empty;
        public string UserContent { get; init; } = string.Empty;
        public CancellationToken CancellationToken { get; init; }
        public IReadOnlyList<LlmMessageAttachment>? Attachments { get; init; }
        public IReadOnlyList<LlmTool>? Tools { get; init; }
        public Func<string, Task>? OnChunk { get; init; }
        public Func<string, Task>? OnReasoning { get; init; }
        public Func<LlmTokenUsage, Task>? OnUsage { get; init; }
        public Func<Task>? OnNativeToolCall { get; init; }
        public Func<string, Task>? OnApiType { get; init; }

        // The generated record formatter would otherwise expose credentials/prompts.
        public override string ToString() => nameof(LlmCompletionRequest);
    }
}
