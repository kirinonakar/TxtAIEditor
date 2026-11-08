using System;
using System.Net.Http;

namespace TxtAIEditor.Core.Services.LLM
{
    internal sealed class LlmResponsesOptions
    {
        public int OutputLimit { get; init; }
        public string? ReasoningEffort { get; init; }
        public string ErrorMessageTemplate { get; init; } = "API call failed ({0}): {1}";
        public string EmptyResponseMessage { get; init; } = string.Empty;
        public Action<HttpRequestMessage>? ConfigureRequest { get; init; }
        public bool AllowExplicitPromptCaching { get; init; } = true;
    }
}
