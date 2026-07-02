using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Core.Interfaces
{
    public interface ILLMService
    {
        Task<string> ExplainCodeAsync(string code, string language, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default);
        Task<string> SummarizeTextAsync(string text, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default);
        Task<string> TranslateTextAsync(string text, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default);
        Task<string> ImproveTextAsync(string text, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default);
        Task<string> CustomPromptAsync(string prompt, string fileContext, string selectedText, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default);
        Task<string> RunAgentAsync(string instruction, string workspaceContext, string selectedText, string mode, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, bool isPlanningMode = false, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, bool hasEnabledSkills = false, bool hasEnabledMcp = false);
        Task<string> RunAgentAsync(EditorSettings settings, string instruction, string workspaceContext, string selectedText, string mode, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, bool isPlanningMode = false, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, bool hasEnabledSkills = false, bool hasEnabledMcp = false);
        
        // Secure API Key handling
        Task SaveApiKeyAsync(string provider, string apiKey);
        Task<string> GetApiKeyAsync(string provider);

        // Exa Search & Content Retrieval Tools
        Task<string> SearchExaAsync(string query, int numResults, CancellationToken cancellationToken = default);
        Task<string> FetchExaAsync(string[] urls, CancellationToken cancellationToken = default);
    }
}
