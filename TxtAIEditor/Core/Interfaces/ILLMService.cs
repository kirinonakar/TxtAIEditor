using System;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Interfaces
{
    public interface ILLMService
    {
        Task<string> ExplainCodeAsync(string code, string language, Func<string, Task>? onChunk = null);
        Task<string> SummarizeTextAsync(string text, Func<string, Task>? onChunk = null);
        Task<string> TranslateTextAsync(string text, Func<string, Task>? onChunk = null);
        Task<string> ImproveTextAsync(string text, Func<string, Task>? onChunk = null);
        Task<string> CustomPromptAsync(string prompt, string fileContext, string selectedText, Func<string, Task>? onChunk = null);
        Task<string> RunAgentAsync(string instruction, string workspaceContext, string selectedText, string mode, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default);
        
        // Secure API Key handling
        Task SaveApiKeyAsync(string provider, string apiKey);
        Task<string> GetApiKeyAsync(string provider);

        // Exa Search & Content Retrieval Tools
        Task<string> SearchExaAsync(string query, int numResults, CancellationToken cancellationToken = default);
        Task<string> FetchExaAsync(string[] urls, CancellationToken cancellationToken = default);
    }
}
